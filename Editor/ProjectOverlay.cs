#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class ProjectOverlay
{
    static bool               _inited;
    static CollabSyncConfig   _cfg;
    static ICollabBackend     _backend;
    static CollabStateDocument _doc = new();
    static readonly Dictionary<string, LockItem> _exactLocks = new(StringComparer.Ordinal);
    static readonly Dictionary<string, LockItem> _scopedObjectLocks = new(StringComparer.Ordinal);
    static readonly List<LockItem> _folderLocks = new();

    static ProjectOverlay()
    {
        EditorApplication.update += EnsureInit;
        EditorApplication.projectWindowItemOnGUI += OnItemGUI;
    }

    static void EnsureInit()
    {
        try
        {
            if (_inited) return;

            _cfg = CollabSyncConfig.LoadOrCreate();
            if (!CollabSyncBackendUtility.TryCreateBackend(_cfg, out _backend, out _, out var statusOrError))
            {
                CollabSyncBackendUtility.LogUnavailableOnce("ProjectOverlay", statusOrError);
                return;
            }

            CollabSyncBackendUtility.ClearLoggedError("ProjectOverlay");
            _doc = CollabSyncEvents.Latest ?? new CollabStateDocument();
            CollabSyncEvents.OnDocUpdate -= OnDocUpdate;
            CollabSyncEvents.OnDocUpdate += OnDocUpdate;
            RefreshNow();

            _inited = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CollabSync] ProjectOverlay init error: {e}");
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
        RebuildLockCache();
        EditorApplication.RepaintProjectWindow();
    }

    static void RebuildLockCache()
    {
        _exactLocks.Clear();
        _scopedObjectLocks.Clear();
        _folderLocks.Clear();

        var now = TimeUtil.NowMs();
        foreach (var lockItem in _doc.locks ?? new List<LockItem>())
        {
            if (lockItem == null || !CollabSyncEditorLockUtility.IsLockActive(lockItem, now))
                continue;
            if (string.IsNullOrEmpty(lockItem.assetPath))
                continue;

            if (lockItem.assetPath.StartsWith("obj:", StringComparison.Ordinal))
            {
                var scopePath = CollabSyncEditorLockUtility.GetLockScopeAssetPath(lockItem);
                if (!string.IsNullOrEmpty(scopePath) && !_scopedObjectLocks.ContainsKey(scopePath))
                    _scopedObjectLocks[scopePath] = lockItem;
                continue;
            }

            if (lockItem.assetPath.EndsWith("/", StringComparison.Ordinal))
                _folderLocks.Add(lockItem);
            else
                _exactLocks[lockItem.assetPath] = lockItem;
        }

        _folderLocks.Sort((a, b) => string.CompareOrdinal(b.assetPath ?? "", a.assetPath ?? ""));
    }

    static void OnItemGUI(string guid, Rect rect)
    {
        try
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;
            if (_exactLocks.Count == 0 && _folderLocks.Count == 0 && _scopedObjectLocks.Count == 0) return;

            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;
            LockItem lockByOther = null;
            LockItem lockByMe = null;

            if (_exactLocks.TryGetValue(path, out var exactLock))
            {
                if (CollabIdentityUtility.Matches(meId, meName, exactLock.ownerId, exactLock.owner))
                    lockByMe = exactLock;
                else
                    lockByOther = exactLock;
            }

            if (lockByOther == null && _scopedObjectLocks.TryGetValue(path, out var scopedObjectLock))
            {
                if (CollabIdentityUtility.Matches(meId, meName, scopedObjectLock.ownerId, scopedObjectLock.owner))
                    lockByMe ??= scopedObjectLock;
                else
                    lockByOther = scopedObjectLock;
            }

            if (lockByOther == null)
            {
                foreach (var folderLock in _folderLocks)
                {
                    if (!CollabSyncEditorLockUtility.DoesLockAffectProjectPath(folderLock, path))
                        continue;

                    if (CollabIdentityUtility.Matches(meId, meName, folderLock.ownerId, folderLock.owner))
                    {
                        lockByMe ??= folderLock;
                    }
                    else
                    {
                        lockByOther = folderLock;
                        break;
                    }
                }
            }

            if (lockByOther != null || lockByMe != null)
            {
                var r = new Rect(rect.xMax - 16, rect.y, 16, 16);
                if (lockByOther != null)
                {
                    var ownerLabel = CollabIdentityUtility.DisplayName(lockByOther.ownerId, lockByOther.owner);
                    GUI.Label(r, new GUIContent("🔒", CollabSyncLocalization.F("Locked by {0}", "{0} がロック中", ownerLabel)));
                }
                else
                {
                    GUI.Label(r, new GUIContent("🔐", CollabSyncLocalization.T("Locked by you", "自分がロック中")));
                }
            }
        }
        catch { }
    }
}
#endif
