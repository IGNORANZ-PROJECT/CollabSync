#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Linq;
using Ignoranz.CollabSync;

public class AssetLockGuard : AssetModificationProcessor
{
    static bool s_isProcessingSave;

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
}
#endif
