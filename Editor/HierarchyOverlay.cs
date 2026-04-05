#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Linq;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class HierarchyOverlay
{
    static bool _inited;
    static CollabSyncConfig   _cfg;
    static ICollabBackend     _backend;
    static CollabStateDocument _doc = new();

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
        EditorApplication.RepaintHierarchyWindow();
    }

    static void OnItemGUI(int instanceID, Rect selectionRect)
    {
        try
        {
            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (!go) return;

            var scenePath = go.scene.path;
            if (string.IsNullOrEmpty(scenePath)) return;
            var objectKey = CollabSyncEditorLockUtility.GetGameObjectLockKey(go);

            long now = TimeUtil.NowMs();
            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;

            bool someoneEditing = _doc.presences.Any(p =>
                string.Equals(p.assetPath, scenePath, StringComparison.Ordinal) &&
                (now - p.heartbeat) < 10_000 &&
                !CollabIdentityUtility.Matches(meId, meName, p.userId, p.user));

            bool AffectsThisItem(LockItem lockItem)
            {
                if (!CollabSyncEditorLockUtility.IsLockActive(lockItem, now))
                    return false;

                if (!string.IsNullOrEmpty(objectKey) &&
                    string.Equals(lockItem.assetPath, objectKey, StringComparison.Ordinal))
                {
                    return true;
                }

                return string.Equals(lockItem.assetPath, scenePath, StringComparison.Ordinal);
            }

            var lockByOther = _doc.locks.FirstOrDefault(l =>
                l != null &&
                AffectsThisItem(l) &&
                !CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner));

            var lockByMe = lockByOther == null
                ? _doc.locks.FirstOrDefault(l =>
                    l != null &&
                    AffectsThisItem(l) &&
                    CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner))
                : null;

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
