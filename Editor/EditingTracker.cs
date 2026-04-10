#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement; // PrefabStage / PrefabStageUtility
using UnityEngine;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class EditingTracker
{
    const double HEARTBEAT_SEC = 5.0;
    const double GIT_HEAD_CHECK_SEC = 2.0;
    const double SCRIPT_POLL_SEC = 0.75;
    const double SCRIPT_OPEN_KEEP_ALIVE_SEC = 20.0;
    const double SCRIPT_EDIT_KEEP_ALIVE_SEC = 20.0;
    const double PUSH_RECOMMENDATION_COOLDOWN_SEC = 60.0;
    const long   AUTO_LOCK_TTL_MS = 15_000;
    const long   WARN_WITHIN_MS = 10_000;

    static bool _initialized;
    static CollabSyncConfig _cfg;
    static ICollabBackend   _backend;

    static string _currentAssetPath = "";
    static string _currentLockKey = "";
    static string _currentTargetName = "";
    static string _currentContext   = "";
    static bool _shouldAutoLock;
    static double _nextBeatAt;
    static double _nextGitHeadCheckAt;
    static double _nextScriptPollAt;
    static string _lastWarnSignature = "";
    static long _lastWarnAt;
    static string _lastAutoLockKey = "";
    static string _lastGitHeadSignature = "";
    static string _lastPushRecommendationKey = "";
    static double _lastPushRecommendationAt;
    static string _trackedScriptAssetPath = "";
    static string _trackedScriptSignature = "";
    static double _scriptAutoLockUntil;
    static string _openedScriptAssetPath = "";
    static double _openedScriptAutoLockUntil;
    static string _transientAssetPath = "";
    static string _transientLockKey = "";
    static string _transientTargetName = "";
    static string _transientContext = "";
    static double _transientAutoLockUntil;
    static readonly object _lastPresenceLock = new();
    static EditingPresence _lastPublishedPresence;

    static EditingTracker()
    {
        try
        {
            EditorApplication.update += OnUpdate;
            EditorSceneManager.activeSceneChangedInEditMode += (_, __) => SafeDetectAll("sceneChanged");
            EditorSceneManager.sceneDirtied += _ => SafeDetectAll("sceneDirtied");
            Selection.selectionChanged += () => SafeDetectAll("selectionChanged");
            PrefabStage.prefabStageOpened  += _ => SafeDetectAll("prefabOpened");
            PrefabStage.prefabStageClosing += _ => SafeDetectAll("prefabClosing");
            Undo.postprocessModifications += OnPostprocessModifications;

            SafeDetectAll("init");
            _nextBeatAt = EditorApplication.timeSinceStartup + HEARTBEAT_SEC;
            _nextGitHeadCheckAt = EditorApplication.timeSinceStartup + GIT_HEAD_CHECK_SEC;
            _nextScriptPollAt = EditorApplication.timeSinceStartup + SCRIPT_POLL_SEC;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] EditingTracker init error: {e}");
        }
    }

    static void EnsureInit()
    {
        if (_initialized) return;

        try
        {
            _cfg = CollabSyncConfig.LoadOrCreate();
            if (!CollabSyncBackendUtility.TryCreateBackend(_cfg, out _backend, out _, out var statusOrError))
            {
                CollabSyncBackendUtility.LogUnavailableOnce("EditingTracker", statusOrError);
                return;
            }

            CollabSyncBackendUtility.ClearLoggedError("EditingTracker");
            _lastGitHeadSignature = ReadGitHeadSignature();
            _initialized = true;

            _ = SafeSendBeat(_currentAssetPath, _currentContext);
            _ = SafeReleaseMergedRetainedLocks();
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] EnsureInit error: {e}");
        }
    }

    static void OnUpdate()
    {
        try
        {
            EnsureInit();

            if (EditorApplication.timeSinceStartup >= _nextScriptPollAt)
            {
                if (HasActiveScriptSelection())
                    SafeDetectAll("scriptPolling");

                _nextScriptPollAt = EditorApplication.timeSinceStartup + SCRIPT_POLL_SEC;
            }

            if (EditorApplication.timeSinceStartup >= _nextGitHeadCheckAt)
            {
                CheckGitHeadChange();
                _ = SafeReleaseMergedRetainedLocks();
                _nextGitHeadCheckAt = EditorApplication.timeSinceStartup + GIT_HEAD_CHECK_SEC;
            }

            if (EditorApplication.timeSinceStartup >= _nextBeatAt)
            {
                SafeDetectAll("heartbeat");

                if (_backend != null)
                    _ = SafeSendBeat(_currentAssetPath, _currentContext);

                _nextBeatAt = EditorApplication.timeSinceStartup + HEARTBEAT_SEC;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] OnUpdate error: {e}");
        }
    }

    [OnOpenAsset(0)]
    static bool OnOpenAsset(int instanceID, int line)
    {
        var obj = EditorUtility.InstanceIDToObject(instanceID);
        var assetPath = AssetDatabase.GetAssetPath(obj);
        if (!IsScriptAssetPath(assetPath))
            return false;

        _openedScriptAssetPath = assetPath;
        _openedScriptAutoLockUntil = EditorApplication.timeSinceStartup + SCRIPT_OPEN_KEEP_ALIVE_SEC;
        RegisterTransientAutoLock(
            Path.GetFileNameWithoutExtension(assetPath),
            assetPath,
            assetPath,
            CollabSyncLocalization.T("Script", "スクリプト"),
            SCRIPT_OPEN_KEEP_ALIVE_SEC);
        SafeDetectAll("scriptOpened");
        return false;
    }

    static void SafeDetectAll(string reason)
    {
        try { DetectAll(reason); }
        catch (Exception e) { Debug.LogError($"[CollabSync] DetectAll error ({reason}): {e}"); }
    }

    static void DetectAll(string reason)
    {
        if (TryGetTransientAutoLockTarget(out var transientTarget))
        {
            SetTarget(
                transientTarget.displayName,
                transientTarget.assetPath,
                transientTarget.lockKey,
                transientTarget.context,
                shouldAutoLock: true);
            return;
        }

        if (CollabSyncEditorLockUtility.TryGetCurrentLockTarget(out var target))
        {
            ApplyScriptAutoLockHeuristics(target);
            SetTarget(target.displayName, target.assetPath, target.lockKey, target.context, target.shouldAutoLock);
            return;
        }

        ClearTrackedScriptTarget();
        SetTarget("", "", "", "", false);
    }

    static void ApplyScriptAutoLockHeuristics(CollabSyncEditorLockUtility.LockTarget target)
    {
        if (target == null)
            return;

        if (!IsScriptAssetPath(target.assetPath))
        {
            ClearTrackedScriptTarget();
            return;
        }

        target.displayName = string.IsNullOrEmpty(target.displayName)
            ? Path.GetFileNameWithoutExtension(target.assetPath)
            : target.displayName;
        target.context = CollabSyncLocalization.T("Script", "スクリプト");
        target.shouldAutoLock = target.shouldAutoLock || ShouldAutoLockScript(target.assetPath);
    }

    static bool ShouldAutoLockScript(string assetPath)
    {
        var now = EditorApplication.timeSinceStartup;
        var signature = GetScriptFileSignature(assetPath);
        if (string.IsNullOrEmpty(signature))
            return ConsumeScriptOpenIntent(assetPath, now);

        if (!string.Equals(_trackedScriptAssetPath, assetPath, StringComparison.Ordinal))
        {
            _trackedScriptAssetPath = assetPath;
            _trackedScriptSignature = signature;
            return ConsumeScriptOpenIntent(assetPath, now);
        }

        if (!string.Equals(_trackedScriptSignature, signature, StringComparison.Ordinal))
        {
            _trackedScriptSignature = signature;
            _scriptAutoLockUntil = now + SCRIPT_EDIT_KEEP_ALIVE_SEC;
            return true;
        }

        if (now <= _scriptAutoLockUntil)
            return true;

        return ConsumeScriptOpenIntent(assetPath, now);
    }

    static bool ConsumeScriptOpenIntent(string assetPath, double now)
    {
        if (!string.Equals(_openedScriptAssetPath, assetPath, StringComparison.Ordinal))
            return false;
        if (now > _openedScriptAutoLockUntil)
            return false;

        _scriptAutoLockUntil = Math.Max(_scriptAutoLockUntil, _openedScriptAutoLockUntil);
        RegisterTransientAutoLock(
            Path.GetFileNameWithoutExtension(assetPath),
            assetPath,
            assetPath,
            CollabSyncLocalization.T("Script", "スクリプト"),
            SCRIPT_OPEN_KEEP_ALIVE_SEC);
        return true;
    }

    static void ClearTrackedScriptTarget()
    {
        _trackedScriptAssetPath = "";
        _trackedScriptSignature = "";
        _scriptAutoLockUntil = 0;
    }

    static bool HasActiveScriptSelection()
    {
        var activeObject = Selection.activeObject;
        if (activeObject == null)
            return false;

        var assetPath = AssetDatabase.GetAssetPath(activeObject);
        return IsScriptAssetPath(assetPath);
    }

    static bool IsScriptAssetPath(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath)
            && assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    internal static void NotifyImportedAssets(string[] importedAssets)
    {
        try
        {
            foreach (var importedPath in importedAssets ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(GetPushRecommendationKey(importedPath, "")))
                    MaybeWarnPushRecommended(importedPath, "", Path.GetFileName(importedPath));
            }

            var importedScripts = (importedAssets ?? Array.Empty<string>())
                .Where(IsScriptAssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (importedScripts.Count == 0)
                return;

            string candidate = null;
            if (importedScripts.Count == 1)
            {
                candidate = importedScripts[0];
            }
            else
            {
                var selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (IsScriptAssetPath(selectedPath))
                    candidate = importedScripts.FirstOrDefault(path => string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));

                if (candidate == null && IsScriptAssetPath(_openedScriptAssetPath))
                    candidate = importedScripts.FirstOrDefault(path => string.Equals(path, _openedScriptAssetPath, StringComparison.OrdinalIgnoreCase));

                if (candidate == null && IsScriptAssetPath(_trackedScriptAssetPath))
                    candidate = importedScripts.FirstOrDefault(path => string.Equals(path, _trackedScriptAssetPath, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrEmpty(candidate))
                return;

            _trackedScriptAssetPath = candidate;
            _trackedScriptSignature = GetScriptFileSignature(candidate);
            _openedScriptAssetPath = candidate;
            _openedScriptAutoLockUntil = EditorApplication.timeSinceStartup + SCRIPT_EDIT_KEEP_ALIVE_SEC;
            RegisterTransientAutoLock(
                Path.GetFileNameWithoutExtension(candidate),
                candidate,
                candidate,
                CollabSyncLocalization.T("Script", "スクリプト"),
                SCRIPT_EDIT_KEEP_ALIVE_SEC);
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] Script import tracking error: {e}");
        }
    }

    static string GetScriptFileSignature(string assetPath)
    {
        if (!IsScriptAssetPath(assetPath))
            return "";

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return "";

            return info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length.ToString();
        }
        catch
        {
            return "";
        }
    }

    static void SetTarget(string displayName, string assetPath, string lockKey, string context, bool shouldAutoLock)
    {
        if (_currentTargetName == displayName &&
            _currentAssetPath == assetPath &&
            _currentLockKey == lockKey &&
            _currentContext == context &&
            _shouldAutoLock == shouldAutoLock)
        {
            return;
        }

        _currentTargetName = displayName;
        _currentAssetPath = assetPath;
        _currentLockKey   = lockKey;
        _currentContext   = context;
        _shouldAutoLock   = shouldAutoLock;

        if (_backend != null)
            _ = SafeSendBeat(_currentAssetPath, _currentContext);

        _nextBeatAt = EditorApplication.timeSinceStartup + HEARTBEAT_SEC;
    }

    static async Task SafeSendBeat(string assetPath, string context)
    {
        try { await SendBeat(assetPath, context); }
        catch (Exception e) { Debug.LogError($"[CollabSync] SendBeat error: {e}"); }
    }

    static async Task SafeSyncAutoLock()
    {
        try { await SyncAutoLockAsync(); }
        catch (Exception e) { Debug.LogError($"[CollabSync] Auto lock sync error: {e}"); }
    }

    static async Task SendBeat(string assetPath, string context)
    {
        if (_backend == null) return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName)) return;

        var p = new EditingPresence
        {
            userId    = meId,
            user      = meName,
            assetPath = assetPath,
            targetKey = string.IsNullOrEmpty(_currentLockKey) ? assetPath : _currentLockKey,
            targetName = _currentTargetName,
            context   = context,
            heartbeat = TimeUtil.NowMs()
        };

        await _backend.PublishPresenceAsync(p);
        RememberLastPublishedPresence(p);
        await SyncAutoLockAsync();

        // 他ユーザーのソフトロック警告
        var doc = CollabSyncEvents.Latest ?? new CollabStateDocument();
        var now = TimeUtil.NowMs();
        string warnSignature = "";
        foreach (var other in doc.presences)
        {
            if (CollabIdentityUtility.Matches(meId, meName, other.userId, other.user)) continue;
            if (!PresenceTargetsConflict(other, assetPath, p.targetKey)) continue;
            if (now - other.heartbeat > WARN_WITHIN_MS) continue;

            var otherName = CollabIdentityUtility.DisplayName(other.userId, other.user);
            warnSignature = (other.userId ?? otherName) + "|" + (p.targetKey ?? assetPath);
            if (_lastWarnSignature != warnSignature || now - _lastWarnAt > WARN_WITHIN_MS)
            {
                Debug.LogWarning($"[CollabSync] {otherName} is editing: {FormatPresenceConflictTarget(p)} (soft lock)");
                _lastWarnSignature = warnSignature;
                _lastWarnAt = now;
            }
            break;
        }

        if (string.IsNullOrEmpty(warnSignature))
            _lastWarnSignature = "";
    }

    static void RememberLastPublishedPresence(EditingPresence presence)
    {
        if (presence == null)
            return;

        lock (_lastPresenceLock)
        {
            _lastPublishedPresence = new EditingPresence
            {
                userId = presence.userId ?? "",
                user = presence.user ?? "",
                assetPath = presence.assetPath ?? "",
                targetKey = presence.targetKey ?? "",
                targetName = presence.targetName ?? "",
                context = presence.context ?? "",
                heartbeat = presence.heartbeat
            };
        }
    }

    public static bool TryGetLastPublishedPresence(out EditingPresence presence)
    {
        lock (_lastPresenceLock)
        {
            if (_lastPublishedPresence == null)
            {
                presence = null;
                return false;
            }

            presence = new EditingPresence
            {
                userId = _lastPublishedPresence.userId ?? "",
                user = _lastPublishedPresence.user ?? "",
                assetPath = _lastPublishedPresence.assetPath ?? "",
                targetKey = _lastPublishedPresence.targetKey ?? "",
                targetName = _lastPublishedPresence.targetName ?? "",
                context = _lastPublishedPresence.context ?? "",
                heartbeat = _lastPublishedPresence.heartbeat
            };
            return true;
        }
    }

    static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
    {
        try
        {
            if (modifications == null)
                return modifications;

            foreach (var modification in modifications)
            {
                var target = modification.currentValue.target ?? modification.previousValue.target;
                if (!TryBuildTransientTargetFromObject(target, out var displayName, out var assetPath, out var lockKey, out var context))
                    continue;

                RegisterTransientAutoLock(displayName, assetPath, lockKey, context, SCRIPT_EDIT_KEEP_ALIVE_SEC);
                break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] Inspector modification tracking error: {e}");
        }

        return modifications;
    }

    static bool TryBuildTransientTargetFromObject(UnityEngine.Object target, out string displayName, out string assetPath, out string lockKey, out string context)
    {
        displayName = "";
        assetPath = "";
        lockKey = "";
        context = "";

        if (target == null)
            return false;

        var component = target as Component;
        var gameObject = target as GameObject ?? component?.gameObject;
        if (gameObject != null)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath) && gameObject.scene == stage.scene)
            {
                displayName = gameObject.name;
                assetPath = stage.assetPath;
                lockKey = CollabSyncEditorLockUtility.GetGameObjectLockKey(gameObject) ?? stage.assetPath;
                context = CollabSyncLocalization.T("Prefab Object", "Prefab オブジェクト");
                return !string.IsNullOrEmpty(lockKey);
            }

            if (gameObject.scene.IsValid() && !string.IsNullOrEmpty(gameObject.scene.path))
            {
                displayName = gameObject.name;
                assetPath = gameObject.scene.path;
                lockKey = CollabSyncEditorLockUtility.GetGameObjectLockKey(gameObject) ?? gameObject.scene.path;
                context = CollabSyncLocalization.T("Scene Object", "シーンオブジェクト");
                return !string.IsNullOrEmpty(lockKey);
            }
        }

        var projectPath = AssetDatabase.GetAssetPath(target);
        if (string.IsNullOrEmpty(projectPath))
        {
            if (IsProjectWideSettingsTarget(target))
            {
                MaybeWarnPushRecommended(
                    "ProjectSettings/" + target.GetType().Name,
                    CollabSyncLocalization.T("Project Settings", "プロジェクト設定"),
                    target.GetType().Name);
                return false;
            }

            if (Selection.activeGameObject == null)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
                {
                    displayName = Path.GetFileNameWithoutExtension(stage.assetPath);
                    assetPath = stage.assetPath;
                    lockKey = stage.assetPath;
                    context = CollabSyncLocalization.T("Prefab Settings", "Prefab 設定");
                    return true;
                }

                var scene = EditorSceneManager.GetActiveScene();
                if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                {
                    displayName = Path.GetFileNameWithoutExtension(scene.path);
                    assetPath = scene.path;
                    lockKey = scene.path;
                    context = CollabSyncLocalization.T("Scene Settings", "シーン設定");
                    return true;
                }
            }

            return false;
        }

        displayName = IsScriptAssetPath(projectPath)
            ? Path.GetFileNameWithoutExtension(projectPath)
            : target.name;
        assetPath = projectPath;
        lockKey = AssetDatabase.IsValidFolder(projectPath) ? projectPath.TrimEnd('/') + "/" : projectPath;
        context = CollabSyncEditorLockUtility.GetAssetContext(projectPath);
        MaybeWarnPushRecommended(projectPath, context, displayName);
        return !string.IsNullOrEmpty(lockKey);
    }

    static void RegisterTransientAutoLock(string displayName, string assetPath, string lockKey, string context, double keepAliveSec)
    {
        if (string.IsNullOrEmpty(lockKey))
            return;

        _transientTargetName = displayName ?? "";
        _transientAssetPath = assetPath ?? "";
        _transientLockKey = lockKey ?? "";
        _transientContext = context ?? "";
        _transientAutoLockUntil = Math.Max(_transientAutoLockUntil, EditorApplication.timeSinceStartup + keepAliveSec);
        MaybeWarnPushRecommended(assetPath, context, displayName);
        SafeDetectAll("transientAutoLock");
    }

    static bool TryGetTransientAutoLockTarget(out CollabSyncEditorLockUtility.LockTarget target)
    {
        target = null;
        if (EditorApplication.timeSinceStartup > _transientAutoLockUntil || string.IsNullOrEmpty(_transientLockKey))
            return false;

        target = new CollabSyncEditorLockUtility.LockTarget
        {
            displayName = _transientTargetName ?? "",
            assetPath = _transientAssetPath ?? "",
            lockKey = _transientLockKey ?? "",
            context = _transientContext ?? "",
            shouldAutoLock = true
        };
        return true;
    }

    static bool PresenceTargetsConflict(EditingPresence other, string assetPath, string targetKey)
    {
        if (other == null)
            return false;

        var myTargetKey = targetKey ?? "";
        var otherTargetKey = other.targetKey ?? "";

        if (!string.IsNullOrEmpty(myTargetKey) && myTargetKey.StartsWith("obj:", StringComparison.Ordinal))
        {
            if (string.Equals(otherTargetKey, myTargetKey, StringComparison.Ordinal))
                return true;

            return (string.IsNullOrEmpty(otherTargetKey)
                    || string.Equals(otherTargetKey, other.assetPath ?? "", StringComparison.Ordinal))
                && string.Equals(other.assetPath ?? "", assetPath ?? "", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(otherTargetKey) && otherTargetKey.StartsWith("obj:", StringComparison.Ordinal))
            return false;

        return string.Equals(other.assetPath ?? "", assetPath ?? "", StringComparison.Ordinal);
    }

    static string FormatPresenceConflictTarget(EditingPresence presence)
    {
        if (presence == null)
            return "";

        if (!string.IsNullOrEmpty(presence.targetName))
            return presence.targetName;
        if (!string.IsNullOrEmpty(presence.assetPath))
            return presence.assetPath;
        return CollabSyncLocalization.T("current target", "現在の対象");
    }

    static void MaybeWarnPushRecommended(string assetPath, string context, string displayName)
    {
        var recommendationKey = GetPushRecommendationKey(assetPath, context);
        if (string.IsNullOrEmpty(recommendationKey))
            return;

        var now = EditorApplication.timeSinceStartup;
        if (string.Equals(_lastPushRecommendationKey, recommendationKey, StringComparison.Ordinal)
            && now - _lastPushRecommendationAt < PUSH_RECOMMENDATION_COOLDOWN_SEC)
        {
            return;
        }

        _lastPushRecommendationKey = recommendationKey;
        _lastPushRecommendationAt = now;

        var label = !string.IsNullOrEmpty(displayName)
            ? displayName
            : (!string.IsNullOrEmpty(assetPath) ? assetPath : recommendationKey);
        Debug.LogWarning($"[CollabSync] {label} affects shared project-wide settings. Push soon after finishing this change to reduce severe conflicts.");
    }

    static string GetPushRecommendationKey(string assetPath, string context)
    {
        var normalizedPath = (assetPath ?? "").Replace('\\', '/');
        if (normalizedPath.StartsWith("ProjectSettings/", StringComparison.Ordinal))
            return normalizedPath;
        if (string.Equals(normalizedPath, "Packages/manifest.json", StringComparison.Ordinal)
            || string.Equals(normalizedPath, "Packages/packages-lock.json", StringComparison.Ordinal))
        {
            return normalizedPath;
        }

        var normalizedContext = (context ?? "").Trim();
        if (string.Equals(normalizedContext, CollabSyncLocalization.T("Project Settings", "プロジェクト設定"), StringComparison.Ordinal)
            || string.Equals(normalizedContext, CollabSyncLocalization.T("Package Manifest", "パッケージ設定"), StringComparison.Ordinal))
        {
            return normalizedContext;
        }

        return "";
    }

    static bool IsProjectWideSettingsTarget(UnityEngine.Object target)
    {
        if (target == null)
            return false;

        var typeName = target.GetType().Name ?? "";
        return typeName.IndexOf("PlayerSettings", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("EditorBuildSettings", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("QualitySettings", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("GraphicsSettings", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("TagManager", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("InputManager", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("AudioManager", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("PhysicsManager", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("ProjectSettings", StringComparison.Ordinal) >= 0;
    }

    static async Task SyncAutoLockAsync()
    {
        if (_backend == null)
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        if (!_shouldAutoLock || string.IsNullOrEmpty(_currentLockKey))
        {
            if (!string.IsNullOrEmpty(_lastAutoLockKey))
                await ReleaseAutoLockIfNeededAsync(_lastAutoLockKey, meId, meName);

            _lastAutoLockKey = "";
            return;
        }

        if (!string.IsNullOrEmpty(_lastAutoLockKey) &&
            !string.Equals(_lastAutoLockKey, _currentLockKey, StringComparison.Ordinal))
        {
            await ReleaseAutoLockIfNeededAsync(_lastAutoLockKey, meId, meName);
        }

        await _backend.TryAcquireLockAsync(_currentLockKey, meId, meName, "auto-lock", AUTO_LOCK_TTL_MS);
        _lastAutoLockKey = _currentLockKey;
    }

    static async Task ReleaseAutoLockIfNeededAsync(string lockKey, string meId, string meName)
    {
        if (_backend == null || string.IsNullOrEmpty(lockKey) || (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName)))
            return;

        var doc = await _backend.LoadOnceAsync() ?? new CollabStateDocument();
        var now = TimeUtil.NowMs();
        var existing = doc.locks.FirstOrDefault(l =>
            l != null &&
            string.Equals(l.assetPath, lockKey, StringComparison.Ordinal) &&
            CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner) &&
            CollabSyncEditorLockUtility.IsLockActive(l, now));

        if (existing == null || !CollabSyncEditorLockUtility.IsAutoLockReason(existing.reason))
            return;

        await _backend.ReleaseLockAsync(lockKey, meId, meName);
    }

    static void CheckGitHeadChange()
    {
        var currentSignature = ReadGitHeadSignature();
        if (string.IsNullOrEmpty(currentSignature))
            return;

        if (string.IsNullOrEmpty(_lastGitHeadSignature))
        {
            _lastGitHeadSignature = currentSignature;
            return;
        }

        if (string.Equals(_lastGitHeadSignature, currentSignature, StringComparison.Ordinal))
            return;

        var shouldCreatePullReminderMemo = IsCommitHeadChange();
        _lastGitHeadSignature = currentSignature;
        _ = SafeHandleGitHeadChange(shouldCreatePullReminderMemo);
    }

    static async Task SafeHandleGitHeadChange(bool createPullReminderMemo)
    {
        try { await HandleGitHeadChangeAsync(createPullReminderMemo); }
        catch (Exception e) { Debug.LogError($"[CollabSync] Git HEAD change handling error: {e}"); }
    }

    static async Task SafeReleaseMergedRetainedLocks()
    {
        try { await ReleaseMergedRetainedLocksAsync(); }
        catch (Exception e) { Debug.LogError($"[CollabSync] Retained lock release error: {e}"); }
    }

    static async Task HandleGitHeadChangeAsync(bool createPullReminderMemo)
    {
        await ReleaseAllAutoLocksOnCommitAsync();
        if (createPullReminderMemo)
            await CreatePullReminderMemoAsync();
    }

    static async Task ReleaseAllAutoLocksOnCommitAsync()
    {
        if (_backend == null)
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        var doc = await _backend.LoadOnceAsync() ?? new CollabStateDocument();
        var keys = (doc.locks ?? new System.Collections.Generic.List<LockItem>())
            .Where(l => l != null &&
                        CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner) &&
                        CollabSyncEditorLockUtility.IsAutoLockReason(l.reason))
            .Select(l => l.assetPath)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToArray();

        foreach (var key in keys)
            await _backend.ReleaseLockAsync(key, meId, meName);

        _lastAutoLockKey = "";
    }

    static async Task ReleaseMergedRetainedLocksAsync()
    {
        if (_backend == null)
            return;
        if (!CollabSyncConfig.IsGitAwareRetainedLocksEnabled())
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        var latest = CollabSyncEvents.Latest ?? new CollabStateDocument();
        var hasRetainedMine = (latest.locks ?? new List<LockItem>())
            .Any(l => l != null
                      && CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner)
                      && CollabSyncGitUtility.IsRetainedLock(l));
        if (!hasRetainedMine)
            return;

        var doc = await _backend.LoadOnceAsync() ?? new CollabStateDocument();
        var keys = (doc.locks ?? new List<LockItem>())
            .Where(l => l != null
                        && CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner)
                        && CollabSyncGitUtility.IsRetainedLock(l)
                        && CollabSyncGitUtility.CanReleaseRetainedLock(l))
            .Select(l => l.assetPath)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToArray();

        foreach (var key in keys)
            await _backend.ReleaseLockAsync(key, meId, meName);
    }

    static async Task CreatePullReminderMemoAsync()
    {
        if (_backend == null)
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        var readByUsers = new List<string>();
        var readByUserIds = new List<string>();
        if (!string.IsNullOrEmpty(meName))
            readByUsers.Add(meName);
        if (!string.IsNullOrEmpty(meId))
            readByUserIds.Add(meId);

        var memo = new MemoItem
        {
            id = Guid.NewGuid().ToString("N"),
            authorId = meId,
            author = meName,
            text = CollabSyncLocalization.T(
                "Changes were pushed. Please pull.",
                "PushされましたPullをしてください"),
            assetPath = "",
            createdAt = TimeUtil.NowMs(),
            pinned = false,
            readByUsers = readByUsers,
            readByUserIds = readByUserIds
        };

        await _backend.UpsertMemoAsync(memo);
    }

    static bool IsCommitHeadChange()
    {
        var action = ReadLatestGitHeadAction();
        return action.StartsWith("commit", StringComparison.OrdinalIgnoreCase);
    }

    static string ReadGitHeadSignature()
    {
        try
        {
            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return "";

            var gitDirectory = ResolveGitDirectory(projectRoot);
            if (string.IsNullOrEmpty(gitDirectory))
                return "";

            var headPath = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(headPath))
                return "";

            var headValue = File.ReadAllText(headPath).Trim();
            if (!headValue.StartsWith("ref:", StringComparison.Ordinal))
                return headValue;

            var refName = headValue.Substring(4).Trim();
            var refPath = Path.Combine(gitDirectory, refName.Replace('/', Path.DirectorySeparatorChar));
            var refValue = File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "";
            return headValue + "|" + refValue;
        }
        catch
        {
            return "";
        }
    }

    static string ReadLatestGitHeadAction()
    {
        try
        {
            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return "";

            var gitDirectory = ResolveGitDirectory(projectRoot);
            if (string.IsNullOrEmpty(gitDirectory))
                return "";

            var headLogPath = Path.Combine(gitDirectory, "logs", "HEAD");
            if (!File.Exists(headLogPath))
                return "";

            var lastLine = File.ReadLines(headLogPath).LastOrDefault();
            if (string.IsNullOrWhiteSpace(lastLine))
                return "";

            var tabIndex = lastLine.LastIndexOf('\t');
            return tabIndex >= 0
                ? lastLine.Substring(tabIndex + 1).Trim()
                : lastLine.Trim();
        }
        catch
        {
            return "";
        }
    }

    static string FindGitProjectRoot(string startDirectory)
    {
        var dir = startDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var dotGit = Path.Combine(dir, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                return dir;

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        return "";
    }

    static string ResolveGitDirectory(string projectRoot)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;

        if (!File.Exists(dotGit))
            return "";

        var content = File.ReadAllText(dotGit).Trim();
        if (!content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            return "";

        var rawPath = content.Substring("gitdir:".Length).Trim();
        if (Path.IsPathRooted(rawPath))
            return rawPath;

        return Path.GetFullPath(Path.Combine(projectRoot, rawPath));
    }
}

class EditingTrackerAssetPostprocessor : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        EditingTracker.NotifyImportedAssets(importedAssets);
    }
}
#endif
