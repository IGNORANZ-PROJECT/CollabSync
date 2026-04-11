#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using System.Linq;
using Ignoranz.CollabSync;

public class AssetLockGuard : AssetModificationProcessor
{
    static bool s_isProcessingSave;
    const long OperationLockTtlMs = 20_000;

    static string[] OnWillSaveAssets(string[] paths)
    {
        if (s_isProcessingSave)
            return paths;

        s_isProcessingSave = true;
        try
        {
            var cfg = CollabSyncConfig.LoadOrCreate();
            if (!cfg)
                return paths;

            if (!CollabSyncBackendUtility.TryCreateBackend(cfg, out var backend, out _, out var statusOrError))
            {
                CollabSyncBackendUtility.LogUnavailableOnce("AssetLockGuard", statusOrError);
                return paths;
            }

            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;
            var doc = backend.LoadOnceAsync().Result ?? new CollabStateDocument();
            var now = TimeUtil.NowMs();
            var active = doc.locks.Where(l => l.ttlMs <= 0 || now - l.createdAt <= l.ttlMs).ToList();

            bool IsLockedByOther(string assetPath)
            {
                foreach (var l in active)
                {
                    if (CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner)) continue;
                    if (l.assetPath.EndsWith("/")) // フォルダロック（prefix）
                    {
                        if (assetPath.StartsWith(l.assetPath)) return true;
                    }
                    else
                    {
                        if (assetPath == l.assetPath) return true;
                    }
                }
                return false;
            }

            var blocked = paths.Where(IsLockedByOther).ToArray();

            if (blocked.Length > 0)
            {
                var list = string.Join("\n", blocked.Select(b =>
                {
                    var ownerLock = active.First(l =>
                        (l.assetPath.EndsWith("/") ? b.StartsWith(l.assetPath) : b == l.assetPath)
                    );
                    var owner = CollabIdentityUtility.DisplayName(ownerLock.ownerId, ownerLock.owner);
                    return CollabSyncLocalization.F("{0}  <- locked by {1}", "{0}  ← {1} がロック中", b, owner);
                }));
                EditorUtility.DisplayDialog(
                    CollabSyncLocalization.T("CollabSync: Save blocked by lock", "CollabSync: ロック中で保存不可"),
                    CollabSyncLocalization.T(
                        "The following assets were blocked from saving because another user currently holds a lock.\n\n",
                        "以下のアセットは他ユーザーがロック中のため保存をブロックしました。\n\n") + list,
                    CollabSyncLocalization.T("OK", "OK"));
                return paths.Except(blocked).ToArray();
            }

            return paths;
        }
        finally
        {
            s_isProcessingSave = false;
        }
    }

    static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
    {
        if (!TryLoadActiveLocks(out var backend, out var activeLocks, out var meId, out var meName))
            return AssetDeleteResult.DidNotDelete;

        bool isFolder = AssetDatabase.IsValidFolder(assetPath);
        if (TryFindBlockingOperationLock(activeLocks, meId, meName, assetPath, isFolder, out var blockingLock))
        {
            ShowBlockedOperationDialog("Delete", assetPath, blockingLock);
            return AssetDeleteResult.FailedDelete;
        }

        TryAcquireOperationLock(backend, meId, meName, assetPath, isFolder);
        return AssetDeleteResult.DidNotDelete;
    }

    static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath)
    {
        if (!TryLoadActiveLocks(out var backend, out var activeLocks, out var meId, out var meName))
            return AssetMoveResult.DidNotMove;

        bool sourceIsFolder = AssetDatabase.IsValidFolder(sourcePath);
        if (TryFindBlockingOperationLock(activeLocks, meId, meName, sourcePath, sourceIsFolder, out var sourceBlockingLock))
        {
            ShowBlockedOperationDialog("Move", sourcePath, sourceBlockingLock);
            return AssetMoveResult.FailedMove;
        }

        if (TryFindBlockingOperationLock(activeLocks, meId, meName, destinationPath, isFolder: false, out var destinationBlockingLock))
        {
            ShowBlockedOperationDialog("Move", destinationPath, destinationBlockingLock);
            return AssetMoveResult.FailedMove;
        }

        TryAcquireOperationLock(backend, meId, meName, sourcePath, sourceIsFolder);
        return AssetMoveResult.DidNotMove;
    }

    static bool TryLoadActiveLocks(out ICollabBackend backend, out List<LockItem> activeLocks, out string meId, out string meName)
    {
        backend = null;
        activeLocks = new List<LockItem>();
        meId = CollabSyncUser.UserId;
        meName = CollabSyncUser.UserName;

        var cfg = CollabSyncConfig.LoadOrCreate();
        if (!cfg)
            return false;

        if (!CollabSyncBackendUtility.TryCreateBackend(cfg, out backend, out _, out var statusOrError))
        {
            CollabSyncBackendUtility.LogUnavailableOnce("AssetLockGuard", statusOrError);
            return false;
        }

        var doc = backend.LoadOnceAsync().Result ?? new CollabStateDocument();
        var now = TimeUtil.NowMs();
        activeLocks = (doc.locks ?? new List<LockItem>())
            .Where(l => l != null && CollabSyncEditorLockUtility.IsLockActive(l, now))
            .ToList();
        return true;
    }

    static bool TryFindBlockingOperationLock(List<LockItem> activeLocks, string meId, string meName, string projectPath, bool isFolder, out LockItem blockingLock)
    {
        blockingLock = null;
        if (string.IsNullOrEmpty(projectPath))
            return false;

        var normalizedPath = projectPath.Replace('\\', '/').TrimEnd('/');
        foreach (var lockItem in activeLocks ?? new List<LockItem>())
        {
            if (lockItem == null || CollabIdentityUtility.Matches(meId, meName, lockItem.ownerId, lockItem.owner))
                continue;

            bool conflicts = isFolder
                ? CollabSyncEditorLockUtility.DoesLockConflictWithFolderPath(lockItem, normalizedPath)
                : CollabSyncEditorLockUtility.DoesLockConflictWithProjectPath(lockItem, normalizedPath);
            if (!conflicts)
                continue;

            blockingLock = lockItem;
            return true;
        }

        return false;
    }

    static void TryAcquireOperationLock(ICollabBackend backend, string meId, string meName, string projectPath, bool isFolder)
    {
        if (backend == null || string.IsNullOrEmpty(projectPath))
            return;

        var normalizedPath = projectPath.Replace('\\', '/');
        var lockKey = isFolder ? normalizedPath.TrimEnd('/') + "/" : normalizedPath;
        try
        {
            backend.TryAcquireLockAsync(lockKey, meId, meName, "asset-operation", OperationLockTtlMs, lockKey)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
        }
    }

    static void ShowBlockedOperationDialog(string operationName, string projectPath, LockItem blockingLock)
    {
        var ownerName = CollabIdentityUtility.DisplayName(blockingLock?.ownerId, blockingLock?.owner);
        var lockLabel = DescribeBlockingLock(blockingLock);
        EditorUtility.DisplayDialog(
            "CollabSync",
            CollabSyncLocalization.F(
                "{0} was blocked because {1} holds a conflicting lock.\n\nTarget: {2}\nLock: {3}",
                "{0} は {1} の競合ロックがあるため中止しました。\n\n対象: {2}\nロック: {3}",
                operationName,
                string.IsNullOrEmpty(ownerName) ? CollabSyncLocalization.T("(unknown)", "(不明)") : ownerName,
                projectPath,
                lockLabel),
            CollabSyncLocalization.T("OK", "OK"));
    }

    static string DescribeBlockingLock(LockItem lockItem)
    {
        if (lockItem == null)
            return "";

        if (CollabSyncEditorLockUtility.IsObjectLockKey(lockItem.assetPath))
        {
            var scopePath = CollabSyncEditorLockUtility.GetLockScopeAssetPath(lockItem);
            return string.IsNullOrEmpty(scopePath)
                ? CollabSyncLocalization.T("Scene/Prefab object lock", "シーン/Prefab オブジェクトロック")
                : CollabSyncLocalization.F(
                    "Object lock in {0}",
                    "{0} 内のオブジェクトロック",
                    scopePath);
        }

        return lockItem.assetPath ?? "";
    }
}
#endif
