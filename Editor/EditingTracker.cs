#if UNITY_EDITOR
using System;
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
    sealed class GitReflogEntryInfo
    {
        public string oldHash = "";
        public string newHash = "";
        public string action = "";
    }

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
                _nextGitHeadCheckAt = EditorApplication.timeSinceStartup + GIT_HEAD_CHECK_SEC;
            }

            if (EditorApplication.timeSinceStartup >= _nextBeatAt)
            {
                SafeDetectAll("heartbeat");

                if (!string.IsNullOrEmpty(_currentAssetPath) && _backend != null)
                    _ = SafeSendBeat(_currentAssetPath, _currentContext);
                else if (_backend != null)
                    _ = SafeSyncAutoLock();

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
        {
            if (!string.IsNullOrEmpty(_currentAssetPath))
                _ = SafeSendBeat(_currentAssetPath, _currentContext);
            else
                _ = SafeSyncAutoLock();
        }

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

        _lastGitHeadSignature = currentSignature;
        var reflog = ReadLatestGitHeadReflogEntry();
        _ = SafeHandleGitHeadChange(currentSignature, reflog);
    }

    static async Task SafeHandleGitHeadChange(string currentSignature, GitReflogEntryInfo reflog)
    {
        try { await HandleGitHeadChangeAsync(currentSignature, reflog); }
        catch (Exception e) { Debug.LogError($"[CollabSync] Git HEAD change handling error: {e}"); }
    }

    static async Task HandleGitHeadChangeAsync(string currentSignature, GitReflogEntryInfo reflog)
    {
        await ReleaseAllAutoLocksOnCommitAsync();

        if (!ShouldBroadcastGitCommitMemo(reflog))
            return;

        await PostGitCommitMemoAsync(currentSignature, reflog);
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

    static bool ShouldBroadcastGitCommitMemo(GitReflogEntryInfo reflog)
    {
        if (reflog == null || string.IsNullOrWhiteSpace(reflog.action))
            return false;

        var action = reflog.action.Trim();
        return action.StartsWith("commit", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("merge", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("cherry-pick", StringComparison.OrdinalIgnoreCase);
    }

    static async Task PostGitCommitMemoAsync(string currentSignature, GitReflogEntryInfo reflog)
    {
        if (_backend == null)
            return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        if (string.IsNullOrEmpty(meId) && string.IsNullOrEmpty(meName))
            return;

        var branchLabel = GetGitHeadDisplayName(currentSignature);
        var shortHash = GetShortGitHash(reflog?.newHash);
        var message = string.IsNullOrEmpty(shortHash)
            ? $"【Git通知】{branchLabel} でコミットを検知しました。最新の変更が共有されていたら Pull をお願いします。"
            : $"【Git通知】{branchLabel} でコミットを検知しました ({shortHash})。最新の変更が共有されていたら Pull をお願いします。";

        var memo = new MemoItem
        {
            id = Guid.NewGuid().ToString("N"),
            authorId = meId,
            author = meName,
            text = message,
            assetPath = "",
            createdAt = TimeUtil.NowMs(),
            pinned = false,
            readByUsers = new System.Collections.Generic.List<string>(),
            readByUserIds = new System.Collections.Generic.List<string>()
        };

        CollabIdentityUtility.AddReadMarker(memo, meId, meName);
        await _backend.UpsertMemoAsync(memo);
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

    static GitReflogEntryInfo ReadLatestGitHeadReflogEntry()
    {
        try
        {
            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return null;

            var gitDirectory = ResolveGitDirectory(projectRoot);
            if (string.IsNullOrEmpty(gitDirectory))
                return null;

            var logHeadPath = Path.Combine(gitDirectory, "logs", "HEAD");
            if (!File.Exists(logHeadPath))
                return null;

            string lastLine = "";
            foreach (var line in File.ReadLines(logHeadPath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }

            if (string.IsNullOrWhiteSpace(lastLine))
                return null;

            return TryParseGitReflogLine(lastLine, out var info) ? info : null;
        }
        catch
        {
            return null;
        }
    }

    static bool TryParseGitReflogLine(string line, out GitReflogEntryInfo info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var tabIndex = line.IndexOf('\t');
        if (tabIndex < 0)
            return false;

        var metadata = line.Substring(0, tabIndex);
        var action = line.Substring(tabIndex + 1).Trim();
        var parts = metadata.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        info = new GitReflogEntryInfo
        {
            oldHash = parts[0],
            newHash = parts[1],
            action = action
        };
        return true;
    }

    static string GetGitHeadDisplayName(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return "HEAD";

        var separatorIndex = signature.IndexOf('|');
        var headPart = separatorIndex >= 0 ? signature.Substring(0, separatorIndex) : signature;
        if (!headPart.StartsWith("ref:", StringComparison.Ordinal))
            return "HEAD";

        var refName = headPart.Substring(4).Trim();
        const string branchPrefix = "refs/heads/";
        if (refName.StartsWith(branchPrefix, StringComparison.Ordinal))
            return refName.Substring(branchPrefix.Length);

        return refName;
    }

    static string GetShortGitHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return "";

        var trimmed = hash.Trim();
        return trimmed.Length <= 7 ? trimmed : trimmed.Substring(0, 7);
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
