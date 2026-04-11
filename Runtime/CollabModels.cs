using System;
using System.Collections.Generic;

namespace Ignoranz.CollabSync
{
    [Serializable]
    public class EditingPresence
    {
        public string userId;
        public string user;
        public string assetPath;
        public string targetKey;
        public string targetName;
        public string context;   // Scene / Prefab / Asset
        public long heartbeat;   // Unix ms
    }

    [Serializable]
    public class MemoItem
    {
        public string id;
        public string authorId;
        public string text;
        public string author;
        public string assetPath;
        public long   createdAt;
        public bool   pinned;
        // JsonUtility互換のため Dictionary ではなく配列で既読ユーザーを保持
        public List<string> readByUsers = new();
        public List<string> readByUserIds = new();
    }

    [Serializable]
    public class LockItem
    {
        public string assetPath;  // プロジェクト相対
        public string scopeAssetPath; // object lock の親 scene/prefab, asset/folder lock の対象パス
        public string ownerId;    // Persistent user ID
        public string owner;      // CollabSyncUser.UserName
        public string reason;
        public long   createdAt;  // Unix ms
        public long   ttlMs;      // 0=無期限
        public string state;      // "" / "retained"
        public long   retainedAt; // Unix ms
        public string gitBranch;
        public string gitHeadCommit;
        public string gitProtectedBranch;
    }

    [Serializable]
    public class WorkHistoryItem
    {
        public string id;
        public string userId;
        public string user;
        public string action;
        public string assetPath;
        public string context;
        public string detail;
        public long   createdAt;
    }

    [Serializable]
    public class CollabStateDocument
    {
        public List<EditingPresence> presences = new();
        public List<MemoItem>        memos     = new();
        public List<LockItem>        locks     = new();
        public List<WorkHistoryItem> history   = new();
        public List<string>          adminUserIds = new();
        public List<string>          adminUsers = new();
        public List<string>          blockedUserIds = new();
        public List<string>          blockedUsers = new();
        public string                rootAdminUserId = "";
        public string                rootAdminUser = "";
        public string                workHistoryMode = "enabled";
        public long                  updatedAt;
    }

    public static class TimeUtil
    {
        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
