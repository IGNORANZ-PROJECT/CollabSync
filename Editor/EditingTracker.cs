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
    const double DIRTY_AUTO_LOCK_KEEP_ALIVE_SEC = 6.0;
    const double SCRIPT_OPEN_KEEP_ALIVE_SEC = 12.0;
    const double SCRIPT_EDIT_KEEP_ALIVE_SEC = 12.0;
    const double TRANSIENT_AUTO_LOCK_KEEP_ALIVE_SEC = 8.0;
    const double PUSH_RECOMMENDATION_COOLDOWN_SEC = 60.0;
    const long   AUTO_LOCK_TTL_MS = 15_000;
    const long   WARN_WITHIN_MS = 10_000;

    sealed class TrackedPropertyChange
    {
        public string baselineSignature = "";
    }

    sealed class AutoLockState
    {
        public string displayName = "";
        public string assetPath = "";
        public string lockKey = "";
        public string context = "";
        public bool requiresDirtyState;
        public double keepAliveUntil;
        public long activityId;
        public readonly Dictionary<string, TrackedPropertyChange> trackedChanges = new(StringComparer.Ordinal);
    }

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
    static string _lastWarnSignature = "";
    static long _lastWarnAt;
    static string _lastGitHeadSignature = "";
    static string _lastPushRecommendationKey = "";
    static double _lastPushRecommendationAt;
    static string _openedScriptAssetPath = "";
    static readonly Dictionary<string, AutoLockState> _autoLockStates = new(StringComparer.Ordinal);
    static readonly Dictionary<string, long> _suppressedAutoLockActivityIds = new(StringComparer.Ordinal);
    static long _nextAutoLockActivityId;
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
        RegisterAutoLockTarget(
            Path.GetFileNameWithoutExtension(assetPath),
            assetPath,
            assetPath,
            CollabSyncLocalization.T("Script", "スクリプト"),
            SCRIPT_OPEN_KEEP_ALIVE_SEC,
            requiresDirtyState: false,
            isFreshActivity: true);
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
        if (CollabSyncEditorLockUtility.TryGetCurrentLockTarget(out var target))
        {
            ApplyScriptAutoLockHeuristics(target);
            if (target.shouldAutoLock && !string.IsNullOrEmpty(target.lockKey))
            {
                RegisterAutoLockTarget(
                    target.displayName,
                    target.assetPath,
                    target.lockKey,
                    target.context,
                    DIRTY_AUTO_LOCK_KEEP_ALIVE_SEC,
                    requiresDirtyState: true,
                    isFreshActivity: false);
            }
            SetTarget(target.displayName, target.assetPath, target.lockKey, target.context, target.shouldAutoLock);
            return;
        }

        SetTarget("", "", "", "", false);
    }

    static void ApplyScriptAutoLockHeuristics(CollabSyncEditorLockUtility.LockTarget target)
    {
        if (target == null)
            return;

        if (!IsScriptAssetPath(target.assetPath))
            return;

        target.displayName = string.IsNullOrEmpty(target.displayName)
            ? Path.GetFileNameWithoutExtension(target.assetPath)
            : target.displayName;
        target.context = CollabSyncLocalization.T("Script", "スクリプト");
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
            }

            if (string.IsNullOrEmpty(candidate))
                return;

            _openedScriptAssetPath = candidate;
            RegisterAutoLockTarget(
                Path.GetFileNameWithoutExtension(candidate),
                candidate,
                candidate,
                CollabSyncLocalization.T("Script", "スクリプト"),
                SCRIPT_EDIT_KEEP_ALIVE_SEC,
                requiresDirtyState: false,
                isFreshActivity: true);
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] Script import tracking error: {e}");
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

            var refreshedLockKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var modification in modifications)
            {
                var target = modification.currentValue.target ?? modification.previousValue.target;
                if (!TryBuildTransientTargetFromObject(target, out var displayName, out var assetPath, out var lockKey, out var context))
                    continue;

                RegisterAutoLockTarget(
                    displayName,
                    assetPath,
                    lockKey,
                    context,
                    TRANSIENT_AUTO_LOCK_KEEP_ALIVE_SEC,
                    requiresDirtyState: true,
                    isFreshActivity: refreshedLockKeys.Add(lockKey));
                TrackPropertyModification(lockKey, modification.previousValue, modification.currentValue);
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

    static void RegisterAutoLockTarget(string displayName, string assetPath, string lockKey, string context, double keepAliveSec, bool requiresDirtyState, bool isFreshActivity)
    {
        if (string.IsNullOrEmpty(lockKey))
            return;

        if (!_autoLockStates.TryGetValue(lockKey, out var state))
        {
            state = new AutoLockState
            {
                lockKey = lockKey,
                activityId = ++_nextAutoLockActivityId
            };
            _autoLockStates[lockKey] = state;
            isFreshActivity = true;
        }
        else if (isFreshActivity)
        {
            state.activityId = ++_nextAutoLockActivityId;
        }

        state.displayName = displayName ?? "";
        state.assetPath = assetPath ?? "";
        state.context = context ?? "";
        state.requiresDirtyState = requiresDirtyState;
        state.keepAliveUntil = Math.Max(state.keepAliveUntil, EditorApplication.timeSinceStartup + keepAliveSec);

        if (isFreshActivity)
            _suppressedAutoLockActivityIds.Remove(lockKey);

        MaybeWarnPushRecommended(assetPath, context, displayName);
    }

    static void TrackPropertyModification(string lockKey, PropertyModification previousValue, PropertyModification currentValue)
    {
        if (string.IsNullOrEmpty(lockKey))
            return;
        if (!_autoLockStates.TryGetValue(lockKey, out var state) || state == null)
            return;

        var propertyKey = GetTrackedPropertyKey(currentValue);
        if (string.IsNullOrEmpty(propertyKey))
            propertyKey = GetTrackedPropertyKey(previousValue);
        if (string.IsNullOrEmpty(propertyKey))
            return;

        var currentSignature = GetTrackedPropertyValueSignature(currentValue);
        if (!state.trackedChanges.TryGetValue(propertyKey, out var trackedChange))
        {
            trackedChange = new TrackedPropertyChange
            {
                baselineSignature = GetTrackedPropertyValueSignature(previousValue)
            };
        }

        if (string.Equals(trackedChange.baselineSignature, currentSignature, StringComparison.Ordinal))
        {
            state.trackedChanges.Remove(propertyKey);
            return;
        }

        state.trackedChanges[propertyKey] = trackedChange;
    }

    static string GetTrackedPropertyKey(PropertyModification modification)
    {
        if (modification == null || modification.target == null || string.IsNullOrEmpty(modification.propertyPath))
            return "";

        var targetKey = GetTrackedUnityObjectKey(modification.target);
        if (string.IsNullOrEmpty(targetKey))
            return "";

        return targetKey + "|" + modification.propertyPath;
    }

    static string GetTrackedPropertyValueSignature(PropertyModification modification)
    {
        if (modification == null)
            return "";

        return (modification.value ?? "") + "|" + GetTrackedUnityObjectKey(modification.objectReference);
    }

    static string GetTrackedUnityObjectKey(UnityEngine.Object target)
    {
        if (target == null)
            return "";

        try
        {
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            if (globalId.identifierType != 0)
                return globalId.ToString();
        }
        catch
        {
        }

        var assetPath = AssetDatabase.GetAssetPath(target);
        if (!string.IsNullOrEmpty(assetPath))
            return "asset:" + assetPath;

        return target.GetType().FullName + "#" + target.GetInstanceID();
    }

    internal static void SuppressAutoLockForKey(string lockKey)
    {
        if (string.IsNullOrEmpty(lockKey))
            return;

        if (_autoLockStates.TryGetValue(lockKey, out var state))
            _suppressedAutoLockActivityIds[lockKey] = state.activityId;
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

    static bool IsAutoLockSuppressed(AutoLockState state)
    {
        return state != null
            && _suppressedAutoLockActivityIds.TryGetValue(state.lockKey, out var suppressedActivityId)
            && suppressedActivityId == state.activityId;
    }

    static void PruneAutoLockStates()
    {
        var now = EditorApplication.timeSinceStartup;
        var removeKeys = new List<string>();

        foreach (var pair in _autoLockStates)
        {
            var state = pair.Value;
            if (state == null)
            {
                removeKeys.Add(pair.Key);
                continue;
            }

            if (IsAutoLockStateStillDirty(state))
                continue;

            if (state.keepAliveUntil <= now)
                removeKeys.Add(pair.Key);
        }

        foreach (var key in removeKeys)
            _autoLockStates.Remove(key);
    }

    static bool IsAutoLockStateStillDirty(AutoLockState state)
    {
        if (state == null || string.IsNullOrEmpty(state.assetPath))
            return false;

        if (state.trackedChanges.Count > 0)
            return true;

        if (state.lockKey.StartsWith("obj:", StringComparison.Ordinal))
            return false;

        if (CollabSyncGitUtility.IsPathModifiedInWorkingTree(state.assetPath))
            return true;

        if (state.assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return IsSceneDirty(state.assetPath);

        if (state.assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return IsPrefabDirty(state.assetPath);

        var asset = AssetDatabase.LoadMainAssetAtPath(state.assetPath);
        return asset != null && EditorUtility.IsDirty(asset);
    }

    static bool IsSceneOrPrefabDirty(string assetPath)
    {
        return assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
            ? IsSceneDirty(assetPath)
            : assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && IsPrefabDirty(assetPath);
    }

    static bool IsSceneDirty(string scenePath)
    {
        var activeScene = EditorSceneManager.GetActiveScene();
        return activeScene.IsValid()
            && string.Equals(activeScene.path, scenePath, StringComparison.Ordinal)
            && activeScene.isDirty;
    }

    static bool IsPrefabDirty(string prefabPath)
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        return stage != null
            && string.Equals(stage.assetPath, prefabPath, StringComparison.Ordinal)
            && stage.scene.IsValid()
            && stage.scene.isDirty;
    }

    static async Task SyncAutoLockAsync()
    {
        if (_backend == null)
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        PruneAutoLockStates();

        var desiredStates = _autoLockStates.Values
            .Where(state => state != null && !IsAutoLockSuppressed(state))
            .ToList();

        foreach (var state in desiredStates)
            await _backend.TryAcquireLockAsync(state.lockKey, meId, meName, "auto-lock", AUTO_LOCK_TTL_MS);

        var desiredKeys = new HashSet<string>(desiredStates.Select(state => state.lockKey), StringComparer.Ordinal);
        var doc = await _backend.LoadOnceAsync() ?? new CollabStateDocument();
        var now = TimeUtil.NowMs();
        var staleAutoLocks = (doc.locks ?? new List<LockItem>())
            .Where(l => l != null
                        && CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner)
                        && CollabSyncEditorLockUtility.IsLockActive(l, now)
                        && CollabSyncEditorLockUtility.IsAutoLockReason(l.reason)
                        && !desiredKeys.Contains(l.assetPath ?? ""))
            .Select(l => l.assetPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct()
            .ToArray();

        foreach (var lockKey in staleAutoLocks)
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

        _autoLockStates.Clear();
        _suppressedAutoLockActivityIds.Clear();
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
