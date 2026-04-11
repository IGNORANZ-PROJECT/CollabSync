#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine;
using Ignoranz.CollabSync;

public static class CollabSyncEditorLockUtility
{
    public sealed class LockTarget
    {
        public string displayName = "";
        public string assetPath = "";
        public string lockKey = "";
        public string context = "";
        public bool shouldAutoLock;
    }

    public static bool TryGetCurrentLockTarget(out LockTarget target)
    {
        target = new LockTarget();

        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        var activeGameObject = Selection.activeGameObject;
        if (activeGameObject != null)
        {
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath) && activeGameObject.scene == stage.scene)
            {
                var objectKey = GetGameObjectLockKey(activeGameObject);
                target.displayName = activeGameObject.name;
                target.assetPath = stage.assetPath;
                target.lockKey = string.IsNullOrEmpty(objectKey) ? stage.assetPath : objectKey;
                target.context = CollabSyncLocalization.T("Prefab Object", "Prefab オブジェクト");
                target.shouldAutoLock = false;
                return true;
            }

            if (activeGameObject.scene.IsValid() && !string.IsNullOrEmpty(activeGameObject.scene.path))
            {
                var objectKey = GetGameObjectLockKey(activeGameObject);
                target.displayName = activeGameObject.name;
                target.assetPath = activeGameObject.scene.path;
                target.lockKey = string.IsNullOrEmpty(objectKey) ? activeGameObject.scene.path : objectKey;
                target.context = CollabSyncLocalization.T("Scene Object", "シーンオブジェクト");
                target.shouldAutoLock = false;
                return true;
            }
        }

        var activeObject = Selection.activeObject;
        if (activeObject != null)
        {
            var assetPath = AssetDatabase.GetAssetPath(activeObject);
            if (!string.IsNullOrEmpty(assetPath))
            {
                bool isFolder = AssetDatabase.IsValidFolder(assetPath);
                target.displayName = activeObject.name;
                target.assetPath = assetPath;
                target.lockKey = isFolder ? assetPath.TrimEnd('/') + "/" : assetPath;
                target.context = GetAssetContext(assetPath);
                target.shouldAutoLock = !isFolder && EditorUtility.IsDirty(activeObject);
                return true;
            }
        }

        if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
        {
            target.displayName = System.IO.Path.GetFileNameWithoutExtension(stage.assetPath);
            target.assetPath = stage.assetPath;
            target.lockKey = stage.assetPath;
            target.context = CollabSyncLocalization.T("Prefab", "Prefab");
            target.shouldAutoLock = false;
            return true;
        }

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
        {
            target.displayName = System.IO.Path.GetFileNameWithoutExtension(scene.path);
            target.assetPath = scene.path;
            target.lockKey = scene.path;
            target.context = CollabSyncLocalization.T("Scene", "シーン");
            target.shouldAutoLock = false;
            return true;
        }

        return false;
    }

    public static bool IsLockActive(LockItem lockItem, long now)
    {
        return lockItem != null && (lockItem.ttlMs <= 0 || now - lockItem.createdAt <= lockItem.ttlMs);
    }

    public static bool IsAutoLockReason(string reason)
    {
        return !string.IsNullOrEmpty(reason) && reason.StartsWith("auto-lock", StringComparison.Ordinal);
    }

    public static bool IsObjectLockKey(string lockKey)
    {
        return !string.IsNullOrEmpty(lockKey) && lockKey.StartsWith("obj:", StringComparison.Ordinal);
    }

    public static string GetGameObjectLockKey(GameObject go)
    {
        try
        {
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            return "obj:" + gid;
        }
        catch
        {
            var path = go != null ? go.scene.path : "";
            if (string.IsNullOrEmpty(path))
                return null;

            return $"obj:{path}#{go.GetInstanceID()}";
        }
    }

    public static string GetGameObjectScopeAssetPath(GameObject go)
    {
        if (go == null)
            return "";

        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && !string.IsNullOrEmpty(stage.assetPath) && go.scene == stage.scene)
            return stage.assetPath;

        return go.scene.IsValid() ? (go.scene.path ?? "") : "";
    }

    public static string GetLockScopeAssetPath(LockItem lockItem)
    {
        if (lockItem == null)
            return "";

        if (!string.IsNullOrEmpty(lockItem.scopeAssetPath))
            return lockItem.scopeAssetPath;

        return lockItem.assetPath ?? "";
    }

    public static bool DoesLockAffectProjectPath(LockItem lockItem, string assetPath)
    {
        if (lockItem == null || string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(lockItem.assetPath))
            return false;

        var normalizedPath = assetPath.Replace('\\', '/');
        if (IsObjectLockKey(lockItem.assetPath))
            return string.Equals(GetLockScopeAssetPath(lockItem), normalizedPath, StringComparison.Ordinal);

        if (lockItem.assetPath.EndsWith("/", StringComparison.Ordinal))
        {
            var folderPath = lockItem.assetPath.TrimEnd('/');
            return string.Equals(normalizedPath, folderPath, StringComparison.Ordinal)
                || normalizedPath.StartsWith(lockItem.assetPath, StringComparison.Ordinal);
        }

        return string.Equals(lockItem.assetPath, normalizedPath, StringComparison.Ordinal);
    }

    public static bool DoesLockConflictWithProjectPath(LockItem lockItem, string assetPath)
    {
        if (lockItem == null || string.IsNullOrEmpty(assetPath))
            return false;

        var normalizedPath = assetPath.Replace('\\', '/');
        if (IsObjectLockKey(lockItem.assetPath))
        {
            var scopePath = GetLockScopeAssetPath(lockItem);
            return !string.IsNullOrEmpty(scopePath)
                && string.Equals(scopePath, normalizedPath, StringComparison.Ordinal);
        }

        return DoesLockAffectProjectPath(lockItem, normalizedPath);
    }

    public static bool DoesLockConflictWithFolderPath(LockItem lockItem, string folderPath)
    {
        if (lockItem == null || string.IsNullOrEmpty(folderPath))
            return false;

        var normalizedFolder = folderPath.Replace('\\', '/').TrimEnd('/');
        var lockPath = lockItem.assetPath ?? "";
        var scopePath = GetLockScopeAssetPath(lockItem).Replace('\\', '/');

        if (IsObjectLockKey(lockPath))
            return !string.IsNullOrEmpty(scopePath)
                && (string.Equals(scopePath, normalizedFolder, StringComparison.Ordinal)
                    || scopePath.StartsWith(normalizedFolder + "/", StringComparison.Ordinal));

        if (lockPath.EndsWith("/", StringComparison.Ordinal))
        {
            var normalizedLockFolder = lockPath.TrimEnd('/');
            return string.Equals(normalizedLockFolder, normalizedFolder, StringComparison.Ordinal)
                || normalizedLockFolder.StartsWith(normalizedFolder + "/", StringComparison.Ordinal)
                || normalizedFolder.StartsWith(normalizedLockFolder + "/", StringComparison.Ordinal);
        }

        return string.Equals(lockPath, normalizedFolder, StringComparison.Ordinal)
            || lockPath.StartsWith(normalizedFolder + "/", StringComparison.Ordinal);
    }

    public static string GetAssetContext(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
            return CollabSyncLocalization.T("Folder", "フォルダ");
        if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return CollabSyncLocalization.T("Script", "スクリプト");
        if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return CollabSyncLocalization.T("Scene", "シーン");
        if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return CollabSyncLocalization.T("Prefab", "Prefab");
        return CollabSyncLocalization.T("Asset", "アセット");
    }
}
#endif
