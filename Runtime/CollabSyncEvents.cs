using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Ignoranz.CollabSync
{
    /// <summary>
    /// CollabSync 全体で使う「ドキュメント更新」イベントハブ。
    /// どのバックエンドからでも RaiseDocUpdate を呼ぶだけでOK。
    /// Editor/Runtime どちらでも安全にメインスレッドへディスパッチする。
    /// </summary>
    public static class CollabSyncEvents
    {
        static readonly object s_dispatchLock = new();
#if UNITY_EDITOR
        static bool s_dispatchScheduled;
        static CollabStateDocument s_pendingDoc;
        static readonly EditorApplication.CallbackFunction s_flushPendingAction = FlushPendingDoc;
#endif

        /// <summary>直近の最新スナップショット（null にはしない）</summary>
        public static CollabStateDocument Latest { get; private set; } = new CollabStateDocument();

        /// <summary>ドキュメント更新（メインスレッドで呼ばれることが保証される）</summary>
        public static event Action<CollabStateDocument> OnDocUpdate;

        /// <summary>
        /// バックエンド側から呼ぶ。スレッドに関係なくOK。
        /// 必ずメインスレッドで OnDocUpdate を発火する。
        /// </summary>
        public static void RaiseDocUpdate(CollabStateDocument doc)
        {
            var safe = doc ?? new CollabStateDocument();

#if UNITY_EDITOR
            bool shouldSchedule = false;
            lock (s_dispatchLock)
            {
                s_pendingDoc = safe;
                if (!s_dispatchScheduled)
                {
                    s_dispatchScheduled = true;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
                EditorApplication.delayCall += s_flushPendingAction;
#else
            // ランタイムの場合：即時呼び出し（必要ならメインスレッドディスパッチャに置き換え）
            InvokeOnMainThread(safe);
#endif
        }

#if UNITY_EDITOR
        static void FlushPendingDoc()
        {
            CollabStateDocument doc;
            lock (s_dispatchLock)
            {
                s_dispatchScheduled = false;
                doc = s_pendingDoc ?? new CollabStateDocument();
                s_pendingDoc = null;
            }

            InvokeOnMainThread(doc);
        }
#endif

        private static void InvokeOnMainThread(CollabStateDocument doc)
        {
            Latest = doc;

            var handlers = OnDocUpdate;
            if (handlers == null) return;

            foreach (Action<CollabStateDocument> h in handlers.GetInvocationList())
            {
                try { h(doc); }
                catch (Exception ex)
                {
#if UNITY_ENGINE || UNITY_EDITOR
                    UnityEngine.Debug.LogError("[CollabSyncEvents] OnDocUpdate handler threw:\n" + ex);
#endif
                }
            }
        }

        /// <summary>購読を全解除（テストや再初期化用）</summary>
        public static void ClearAllSubscribers()
        {
            if (OnDocUpdate == null) return;
            foreach (var d in OnDocUpdate.GetInvocationList())
                OnDocUpdate -= (Action<CollabStateDocument>)d;

#if UNITY_EDITOR
            lock (s_dispatchLock)
            {
                s_dispatchScheduled = false;
                s_pendingDoc = null;
            }
#endif
        }
    }
}
