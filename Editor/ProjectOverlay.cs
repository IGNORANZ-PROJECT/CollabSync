#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Linq;
using Ignoranz.CollabSync;

[InitializeOnLoad]
public static class ProjectOverlay
{
    static bool               _inited;
    static CollabSyncConfig   _cfg;
    static ICollabBackend     _backend;
    static CollabStateDocument _doc = new();

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
        EditorApplication.RepaintProjectWindow();
    }

    static void OnItemGUI(string guid, Rect rect)
    {
        try
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;

            long now = TimeUtil.NowMs();
            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;

            var lockByOther = _doc.locks.FirstOrDefault(l =>
                l != null &&
                CollabSyncEditorLockUtility.IsLockActive(l, now) &&
                CollabSyncEditorLockUtility.DoesLockAffectProjectPath(l, path) &&
                !CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner));

            var lockByMe = lockByOther == null
                ? _doc.locks.FirstOrDefault(l =>
                    l != null &&
                    CollabSyncEditorLockUtility.IsLockActive(l, now) &&
                    CollabSyncEditorLockUtility.DoesLockAffectProjectPath(l, path) &&
                    CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner))
                : null;

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
