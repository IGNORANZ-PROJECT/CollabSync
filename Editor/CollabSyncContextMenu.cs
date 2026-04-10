#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using Ignoranz.CollabSync;

public static class CollabSyncContextMenu
{
    // =========================
    // Assets メニュー（Project）
    // =========================

    [MenuItem("Assets/CollabSync/Lock", priority = 1900)]
    public static void LockSelection()
    {
        if (!TryGetBackend(out var backend)) return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        var targets = GetSelectedAssetPaths();

        foreach (var path in targets)
        {
            bool isFolder = AssetDatabase.IsValidFolder(path);
            string lockKey = isFolder ? (path.TrimEnd('/') + "/") : path;
            _ = backend.TryAcquireLockAsync(lockKey, meId, meName, reason: "context-menu", ttlMs: 0);
        }
    }

    [MenuItem("Assets/CollabSync/Unlock", priority = 1901)]
    public static void UnlockSelection()
    {
        if (!TryGetBackend(out var backend)) return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        var targets = GetSelectedAssetPaths();

        foreach (var path in targets)
        {
            bool isFolder = AssetDatabase.IsValidFolder(path);
            string lockKey = isFolder ? (path.TrimEnd('/') + "/") : path;
            EditingTracker.SuppressAutoLockForKey(lockKey);
            _ = backend.ReleaseLockAsync(lockKey, meId, meName);
        }
    }

    // Option + Shift + L でトグル（Project ウィンドウの選択に対して）
    [MenuItem("Assets/CollabSync/Toggle Lock &#l", priority = 1902)]
    public static async void ToggleAssetLocks()
    {
        if (!TryGetBackend(out var backend)) return;

        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;
        var paths = GetSelectedAssetPaths();
        if (paths.Count == 0) return;

        var doc = await backend.LoadOnceAsync();
        long now = TimeUtil.NowMs();

        // どれか一つでも他人のロックがあれば無視（自分のロックなら解除）
        foreach (var p in paths)
        {
            bool isFolder = AssetDatabase.IsValidFolder(p);
            string key = isFolder ? (p.TrimEnd('/') + "/") : p;

            var existing = doc.locks.FirstOrDefault(l =>
            {
                bool active = (l.ttlMs <= 0 || now - l.createdAt <= l.ttlMs);
                return active && l.assetPath == key;
            });

            if (existing == null)
            {
                // ロックが無ければ取る
                _ = backend.TryAcquireLockAsync(key, meId, meName, reason: "context-menu", ttlMs: 0);
            }
            else
            {
                // あれば自分のだけ解除（他人のは触らない）
                if (CollabIdentityUtility.Matches(meId, meName, existing.ownerId, existing.owner))
                {
                    EditingTracker.SuppressAutoLockForKey(key);
                    _ = backend.ReleaseLockAsync(key, meId, meName);
                }
            }
        }
    }

    [MenuItem("Assets/CollabSync/Lock", validate = true)]
    [MenuItem("Assets/CollabSync/Unlock", validate = true)]
    [MenuItem("Assets/CollabSync/Toggle Lock &#l", validate = true)]
    private static bool ValidateAssets() => Selection.objects != null && Selection.objects.Length > 0;

    private static List<string> GetSelectedAssetPaths()
    {
        var list = new List<string>();
        foreach (var obj in Selection.objects)
        {
            var p = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(p)) list.Add(p);
        }
        return list.Distinct().ToList();
    }

    // ==================================
    // GameObject メニュー（Hierarchy）
    // ==================================

    [MenuItem("GameObject/CollabSync/Lock Object (and Scripts)", priority = 49)]
    public static void LockSelectedObjects()
    {
        if (!TryGetBackend(out var backend)) return;
        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;

        foreach (var go in Selection.gameObjects)
        {
            // 1) オブジェクト自体をグローバルIDでロック
            string gidKey = GetGameObjectLockKey(go);
            if (!string.IsNullOrEmpty(gidKey))
                _ = backend.TryAcquireLockAsync(gidKey, meId, meName, reason: "object-lock", ttlMs: 0);

            // 2) 付いている MonoBehaviour のスクリプト .cs もロック
            foreach (var csPath in GetAttachedMonoScriptPaths(go))
            {
                _ = backend.TryAcquireLockAsync(csPath, meId, meName, reason: "component-script", ttlMs: 0);
            }
        }
    }

    [MenuItem("GameObject/CollabSync/Unlock Object (and Scripts)", priority = 50)]
    public static void UnlockSelectedObjects()
    {
        if (!TryGetBackend(out var backend)) return;
        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;

        foreach (var go in Selection.gameObjects)
        {
            // 1) オブジェクト自体
            string gidKey = GetGameObjectLockKey(go);
            if (!string.IsNullOrEmpty(gidKey))
            {
                EditingTracker.SuppressAutoLockForKey(gidKey);
                _ = backend.ReleaseLockAsync(gidKey, meId, meName);
            }

            // 2) 付いている MonoBehaviour のスクリプト .cs
            foreach (var csPath in GetAttachedMonoScriptPaths(go))
            {
                EditingTracker.SuppressAutoLockForKey(csPath);
                _ = backend.ReleaseLockAsync(csPath, meId, meName);
            }
        }
    }

    // Option + Shift + L でトグル（Hierarchy の選択に対して）
    [MenuItem("GameObject/CollabSync/Toggle Object Lock (and Scripts) ^#l", priority = 51)]
    public static async void ToggleSelectedObjects()
    {
        if (!TryGetBackend(out var backend)) return;
        var meId = CollabSyncUser.UserId;
        var meName = CollabSyncUser.UserName;

        // 最新ロック取得
        var doc = await backend.LoadOnceAsync();
        long now = TimeUtil.NowMs();

        foreach (var go in Selection.gameObjects)
        {
            var keys = new List<string>();

            // オブジェクト自体
            string gidKey = GetGameObjectLockKey(go);
            if (!string.IsNullOrEmpty(gidKey)) keys.Add(gidKey);

            // アタッチされた全スクリプト .cs
            keys.AddRange(GetAttachedMonoScriptPaths(go));

            // オブジェクト基準で「自分のロックがあれば解除」「無ければ取得」
            bool hasAnyActiveMine = keys.Any(k =>
                doc.locks.Any(l =>
                    l.assetPath == k &&
                    CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner) &&
                    (l.ttlMs <= 0 || now - l.createdAt <= l.ttlMs)));

            if (hasAnyActiveMine)
            {
                foreach (var k in keys)
                {
                    EditingTracker.SuppressAutoLockForKey(k);
                    _ = backend.ReleaseLockAsync(k, meId, meName);
                }
            }
            else
            {
                foreach (var k in keys) _ = backend.TryAcquireLockAsync(k, meId, meName, reason: "object-lock", ttlMs: 0);
            }
        }
    }

    [MenuItem("GameObject/CollabSync/Lock Object (and Scripts)", validate = true)]
    [MenuItem("GameObject/CollabSync/Unlock Object (and Scripts)", validate = true)]
    [MenuItem("GameObject/CollabSync/Toggle Object Lock (and Scripts) ^#l", validate = true)]
    private static bool ValidateObjects() => Selection.gameObjects != null && Selection.gameObjects.Length > 0;

    // ===== Helpers =====

    private static string GetGameObjectLockKey(GameObject go)
    {
        return CollabSyncEditorLockUtility.GetGameObjectLockKey(go);
    }

    private static IEnumerable<string> GetAttachedMonoScriptPaths(GameObject go)
    {
        // そのオブジェクトの全コンポーネントから MonoBehaviour を抽出して .cs パス化
        var comps = go.GetComponents<Component>();
        foreach (var c in comps)
        {
            var mb = c as MonoBehaviour;
            if (mb == null) continue;

            var script = MonoScript.FromMonoBehaviour(mb);
            if (script == null) continue;

            var path = AssetDatabase.GetAssetPath(script);
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    private static bool TryGetBackend(out ICollabBackend backend)
    {
        var cfg = CollabSyncConfig.LoadOrCreate();
        if (CollabSyncBackendUtility.TryCreateBackend(cfg, out backend, out _, out var statusOrError))
            return true;

        backend = null;
        EditorUtility.DisplayDialog("CollabSync", statusOrError, CollabSyncLocalization.T("OK", "OK"));
        return false;
    }
}
#endif
