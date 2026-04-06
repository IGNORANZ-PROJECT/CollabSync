#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement; // PrefabStage / PrefabStageUtility
using UnityEngine;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class EditingTracker
{
    const double HEARTBEAT_SEC = 5.0;
    const double GIT_HEAD_CHECK_SEC = 2.0;
    const long   AUTO_LOCK_TTL_MS = 15_000;
    const long   WARN_WITHIN_MS = 10_000;

    static bool _initialized;
    static CollabSyncConfig _cfg;
    static ICollabBackend   _backend;

    static string _currentAssetPath = "";
    static string _currentLockKey = "";
    static string _currentContext   = "";
    static bool _shouldAutoLock;
    static double _nextBeatAt;
    static double _nextGitHeadCheckAt;
    static string _lastWarnSignature = "";
    static long _lastWarnAt;
    static string _lastAutoLockKey = "";
    static string _lastGitHeadSignature = "";
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

    static void SafeDetectAll(string reason)
    {
        try { DetectAll(reason); }
        catch (Exception e) { Debug.LogError($"[CollabSync] DetectAll error ({reason}): {e}"); }
    }

    static void DetectAll(string reason)
    {
        if (CollabSyncEditorLockUtility.TryGetCurrentLockTarget(out var target))
        {
            SetTarget(target.assetPath, target.lockKey, target.context, target.shouldAutoLock);
            return;
        }

        SetTarget("", "", "", false);
    }

    static void SetTarget(string assetPath, string lockKey, string context, bool shouldAutoLock)
    {
        if (_currentAssetPath == assetPath &&
            _currentLockKey == lockKey &&
            _currentContext == context &&
            _shouldAutoLock == shouldAutoLock)
        {
            return;
        }

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
            if (!string.Equals(other.assetPath, assetPath, StringComparison.Ordinal)) continue;
            if (now - other.heartbeat > WARN_WITHIN_MS) continue;

            var otherName = CollabIdentityUtility.DisplayName(other.userId, other.user);
            warnSignature = (other.userId ?? otherName) + "|" + assetPath;
            if (_lastWarnSignature != warnSignature || now - _lastWarnAt > WARN_WITHIN_MS)
            {
                Debug.LogWarning($"[CollabSync] {otherName} is editing: {assetPath} (soft lock)");
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
                context = _lastPublishedPresence.context ?? "",
                heartbeat = _lastPublishedPresence.heartbeat
            };
            return true;
        }
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
#endif
