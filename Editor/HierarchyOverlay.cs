#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class HierarchyOverlay
{
    static bool _inited;
    static CollabSyncConfig   _cfg;
    static ICollabBackend     _backend;
    static CollabStateDocument _doc = new();
    static readonly Dictionary<string, HashSet<string>> _presenceUsersByScene = new(StringComparer.Ordinal);
    static readonly Dictionary<string, LockItem> _activeLocksByPath = new(StringComparer.Ordinal);
    static bool _hasObjectLocks;

    static HierarchyOverlay()
    {
        EditorApplication.update += EnsureInit;
        EditorApplication.hierarchyWindowItemOnGUI += OnItemGUI;
    }

    static void EnsureInit()
    {
        try
        {
            if (_inited) return;

            _cfg = CollabSyncConfig.LoadOrCreate();
            if (!CollabSyncBackendUtility.TryCreateBackend(_cfg, out _backend, out _, out var statusOrError))
            {
                CollabSyncBackendUtility.LogUnavailableOnce("HierarchyOverlay", statusOrError);
                return;
            }

            CollabSyncBackendUtility.ClearLoggedError("HierarchyOverlay");
            _doc = CollabSyncEvents.Latest ?? new CollabStateDocument();
            CollabSyncEvents.OnDocUpdate -= OnDocUpdate;
            CollabSyncEvents.OnDocUpdate += OnDocUpdate;
            RefreshNow();

            _inited = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] HierarchyOverlay init error: {e}");
            _inited = false;
        }
        finally
        {
            if (_inited) EditorApplication.update -= EnsureInit;
        }
    }

    static async void RefreshNow()
    {
        if (_backend == null) return;
        var doc = await _backend.LoadOnceAsync();
        OnDocUpdate(doc ?? new CollabStateDocument());
    }

    static void OnDocUpdate(CollabStateDocument doc)
    {
        _doc = doc ?? new CollabStateDocument();
        RebuildCaches();
        EditorApplication.RepaintHierarchyWindow();
    }

    static void RebuildCaches()
    {
        _presenceUsersByScene.Clear();
        _activeLocksByPath.Clear();
        _hasObjectLocks = false;

        var now = TimeUtil.NowMs();
        foreach (var presence in _doc.presences ?? new List<EditingPresence>())
        {
            if (presence == null || string.IsNullOrEmpty(presence.assetPath))
                continue;
            if (now - presence.heartbeat >= 10_000)
                continue;

            if (!_presenceUsersByScene.TryGetValue(presence.assetPath, out var users))
            {
                users = new HashSet<string>(StringComparer.Ordinal);
                _presenceUsersByScene[presence.assetPath] = users;
            }

            users.Add(GetUserKey(presence.userId, presence.user));
        }

        foreach (var lockItem in _doc.locks ?? new List<LockItem>())
        {
            if (lockItem == null || string.IsNullOrEmpty(lockItem.assetPath))
                continue;
            if (!CollabSyncEditorLockUtility.IsLockActive(lockItem, now))
                continue;

            _activeLocksByPath[lockItem.assetPath] = lockItem;
            _hasObjectLocks |= lockItem.assetPath.StartsWith("obj:", StringComparison.Ordinal);
        }
    }

    static string GetUserKey(string userId, string userName)
    {
        userId = CollabIdentityUtility.Normalize(userId);
        return string.IsNullOrEmpty(userId)
            ? "legacy:" + CollabIdentityUtility.DisplayName(userId, userName)
            : userId;
    }

    static void OnItemGUI(int instanceID, Rect selectionRect)
    {
        try
        {
            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (!go) return;

            var scenePath = go.scene.path;
            if (string.IsNullOrEmpty(scenePath)) return;
            if (!_presenceUsersByScene.ContainsKey(scenePath) && !_activeLocksByPath.ContainsKey(scenePath) && !_hasObjectLocks)
                return;

            var objectKey = _hasObjectLocks ? CollabSyncEditorLockUtility.GetGameObjectLockKey(go) : null;

            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;
            var meKey = GetUserKey(meId, meName);

            bool someoneEditing = false;
            if (_presenceUsersByScene.TryGetValue(scenePath, out var presenceUsers))
            {
                foreach (var userKey in presenceUsers)
                {
                    if (!string.Equals(userKey, meKey, StringComparison.Ordinal))
                    {
                        someoneEditing = true;
                        break;
                    }
                }
            }

            LockItem lockByOther = null;
            LockItem lockByMe = null;
            if (!string.IsNullOrEmpty(objectKey) && _activeLocksByPath.TryGetValue(objectKey, out var objectLock))
            {
                if (CollabIdentityUtility.Matches(meId, meName, objectLock.ownerId, objectLock.owner))
                    lockByMe = objectLock;
                else
                    lockByOther = objectLock;
            }

            if (lockByOther == null && _activeLocksByPath.TryGetValue(scenePath, out var sceneLock))
            {
                if (CollabIdentityUtility.Matches(meId, meName, sceneLock.ownerId, sceneLock.owner))
                    lockByMe ??= sceneLock;
                else
                    lockByOther = sceneLock;
            }

            if (lockByOther != null || lockByMe != null || someoneEditing)
            {
                var r = new Rect(selectionRect.xMax - 18, selectionRect.y, 16, 16);
                string icon;
                string tooltip;

                if (lockByOther != null)
                {
                    icon = "🔒";
                    tooltip = CollabSyncLocalization.F(
                        "Locked by {0}",
                        "{0} がロック中",
                        CollabIdentityUtility.DisplayName(lockByOther.ownerId, lockByOther.owner));
                }
                else if (lockByMe != null)
                {
                    icon = "🔐";
                    tooltip = CollabSyncLocalization.T("Locked by you", "自分がロック中");
                }
                else
                {
                    icon = "⚠";
                    tooltip = CollabSyncLocalization.T("Being edited by teammate", "他ユーザーが編集中");
                }

                GUI.Label(r, new GUIContent(icon, tooltip));
            }
        }
        catch { }
    }
}
#endif
