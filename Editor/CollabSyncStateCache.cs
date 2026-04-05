#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;

namespace Ignoranz.CollabSync
{
    [InitializeOnLoad]
    public static class CollabSyncStateCache
    {
        static readonly object _lock = new();
        static bool _subscribed;
        static ICollabBackend _backend;

        static CollabSyncStateCache()
        {
            // Editor開始時に遅延初期化（必要な時点でEnsureSubscribedが呼ばれる想定）
        }

        /// <summary>一度だけ購読を張る（何度呼んでもOK）</summary>
        public static void EnsureSubscribed()
        {
            lock (_lock)
            {
                if (_subscribed) return;

                var cfg = CollabSyncConfig.LoadOrCreate();
                if (!CollabSyncBackendUtility.TryCreateBackend(cfg, out _backend, out _, out var statusOrError))
                {
                    CollabSyncBackendUtility.LogUnavailableOnce("CollabSyncStateCache", statusOrError);
                    return;
                }

                CollabSyncBackendUtility.ClearLoggedError("CollabSyncStateCache");

                // バックエンドから更新を受けて全体イベントへ中継
                _backend.Subscribe(d => CollabSyncEvents.RaiseDocUpdate(d));

                // 起動直後の即時同期
                _ = InitialLoad();

                _subscribed = true;
            }
        }

        static async Task InitialLoad()
        {
            try
            {
                var d = await _backend.LoadOnceAsync();
                CollabSyncEvents.RaiseDocUpdate(d ?? new CollabStateDocument());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[CollabSyncStateCache] InitialLoad error: " + e);
            }
        }

        /// <summary>明示的に最新のスナップショットを取得して配信</summary>
        public static async Task RefreshNow()
        {
            if (_backend == null) return;
            try
            {
                var d = await _backend.LoadOnceAsync();
                CollabSyncEvents.RaiseDocUpdate(d ?? new CollabStateDocument());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[CollabSyncStateCache] RefreshNow error: " + e);
            }
        }
    }
}
#endif
