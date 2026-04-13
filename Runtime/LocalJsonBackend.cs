using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Ignoranz.CollabSync
{
    public class LocalJsonBackend : ICollabBackend, IDisposable
    {
        const int FileAccessRetryCount = 40;
        const int FileAccessRetryDelayMs = 100;
        const int RaiseDebounceDelayMs = 75;
        const int MaxBackupFiles = 24;
        const int MaxHistoryItems = 160;
        const long HistoryDuplicateWindowMs = 3000;
        static readonly TimeSpan BackupMinInterval = TimeSpan.FromMinutes(15);
        const string ShardManifestFormat = "collabsync-shard-store-v1";

        readonly string _path;
        readonly string _projectId;
        readonly bool _protectSharedStateFile;
        readonly string _storeDirectory;
        readonly string _storeManifestPath;
        readonly string _backupDirectory;
        readonly string _backupPrefix;
        readonly object _mutationLock = new();
        FileSystemWatcher _watcher;
        Action<CollabStateDocument> _onUpdate;
        bool _disposed;
        int _raiseVersion;
        long _lastRaisedUpdatedAt = long.MinValue;

        [Serializable]
        sealed class ShardStoreManifest
        {
            public string format = ShardManifestFormat;
            public string storeDirectoryName = "";
            public bool protectSharedStateFile = true;
            public long updatedAt;
        }

        [Serializable]
        sealed class SharedStateRecord
        {
            public string rootAdminUserId = "";
            public string rootAdminUser = "";
            public string workHistoryMode = "enabled";
            public long updatedAt;
        }

        [Serializable]
        sealed class IdentityRecord
        {
            public string userId = "";
            public string userName = "";
            public long updatedAt;
            public bool deleted;
        }

        [Serializable]
        sealed class PresenceRecord
        {
            public EditingPresence presence = new();
            public long updatedAt;
            public bool deleted;
        }

        [Serializable]
        sealed class LockRecord
        {
            public LockItem item = new();
            public long updatedAt;
            public bool deleted;
        }

        [Serializable]
        sealed class MemoRecord
        {
            public MemoItem item = new();
            public long updatedAt;
            public bool deleted;
        }

        [Serializable]
        sealed class MemoReadRecord
        {
            public string memoId = "";
            public string userId = "";
            public string userName = "";
            public long updatedAt;
            public bool deleted;
        }

        [Serializable]
        sealed class HistoryRecord
        {
            public WorkHistoryItem item = new();
            public long updatedAt;
        }

        public LocalJsonBackend(string jsonPath, string projectId = "", bool protectSharedStateFile = true)
        {
            _path = string.IsNullOrWhiteSpace(jsonPath)
                ? CollabSyncBackendUtility.DefaultLocalJsonPath
                : jsonPath;
            _projectId = CollabSyncProtectedStateUtility.NormalizeProjectId(projectId);
            _protectSharedStateFile = protectSharedStateFile;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _storeDirectory = Path.Combine(dir ?? ".", Path.GetFileNameWithoutExtension(_path) + ".collabsync-store");
            _storeManifestPath = _path;

            _backupDirectory = Path.Combine(dir ?? ".", ".collabsync-backups");
            _backupPrefix = Path.GetFileNameWithoutExtension(_path) + "-";

            EnsureJsonFileExists();
            EnsureStorageProtectionMode();

            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path) ?? ".", "*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true
                };
                _watcher.Changed += (_, __) => RequestRaise();
                _watcher.Created += (_, __) => RequestRaise();
                _watcher.Deleted += (_, __) => RequestRaise();
                _watcher.Renamed += (_, __) => RequestRaise();
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
            }
        }

        static string ToJson(CollabStateDocument doc) =>
#if UNITY_2021_2_OR_NEWER
            UnityEngine.JsonUtility.ToJson(doc, true);
#else
            UnityEngine.JsonUtility.ToJson(doc);
#endif

        static string ToJsonObject<T>(T value)
        {
#if UNITY_2021_2_OR_NEWER
            return UnityEngine.JsonUtility.ToJson(value, true);
#else
            return UnityEngine.JsonUtility.ToJson(value);
#endif
        }

        static void Normalize(CollabStateDocument doc)
        {
            doc ??= new CollabStateDocument();
            doc.presences ??= new List<EditingPresence>();
            doc.memos ??= new List<MemoItem>();
            doc.locks ??= new List<LockItem>();
            doc.history ??= new List<WorkHistoryItem>();
            doc.adminUserIds ??= new List<string>();
            doc.adminUsers ??= new List<string>();
            doc.blockedUserIds ??= new List<string>();
            doc.blockedUsers ??= new List<string>();
            doc.rootAdminUserId ??= "";
            doc.rootAdminUser ??= "";
            doc.workHistoryMode = string.Equals(doc.workHistoryMode, "disabled", StringComparison.Ordinal)
                ? "disabled"
                : "enabled";

            while (doc.adminUserIds.Count < doc.adminUsers.Count)
                doc.adminUserIds.Add("");
            while (doc.blockedUserIds.Count < doc.blockedUsers.Count)
                doc.blockedUserIds.Add("");

            if (string.IsNullOrWhiteSpace(doc.rootAdminUser) && doc.adminUsers.Count > 0)
                doc.rootAdminUser = doc.adminUsers[0] ?? "";
            if (string.IsNullOrWhiteSpace(doc.rootAdminUserId) && doc.adminUserIds.Count > 0)
                doc.rootAdminUserId = doc.adminUserIds[0] ?? "";

            for (int i = doc.adminUsers.Count - 1; i >= 0; i--)
            {
                var adminName = CollabIdentityUtility.Normalize(doc.adminUsers[i]);
                var adminId = i < doc.adminUserIds.Count ? CollabIdentityUtility.Normalize(doc.adminUserIds[i]) : "";
                if (string.IsNullOrEmpty(adminName) && string.IsNullOrEmpty(adminId))
                {
                    doc.adminUsers.RemoveAt(i);
                    if (i < doc.adminUserIds.Count)
                        doc.adminUserIds.RemoveAt(i);
                }
            }

            for (int i = doc.blockedUsers.Count - 1; i >= 0; i--)
            {
                var blockedName = CollabIdentityUtility.Normalize(doc.blockedUsers[i]);
                var blockedId = i < doc.blockedUserIds.Count ? CollabIdentityUtility.Normalize(doc.blockedUserIds[i]) : "";
                if (string.IsNullOrEmpty(blockedName) && string.IsNullOrEmpty(blockedId))
                {
                    doc.blockedUsers.RemoveAt(i);
                    if (i < doc.blockedUserIds.Count)
                        doc.blockedUserIds.RemoveAt(i);
                }
            }

            foreach (var presence in doc.presences)
            {
                presence.userId ??= "";
                presence.user ??= "";
                presence.assetPath ??= "";
                presence.targetKey ??= "";
                presence.targetName ??= "";
                presence.context ??= "";
            }

            foreach (var memo in doc.memos)
            {
                memo.authorId ??= "";
                memo.author ??= "";
                CollabIdentityUtility.EnsureReadBy(memo);
            }

            foreach (var lockItem in doc.locks)
            {
                lockItem.assetPath ??= "";
                lockItem.scopeAssetPath ??= "";
                lockItem.ownerId ??= "";
                lockItem.owner ??= "";
                lockItem.reason ??= "";
                lockItem.state = string.Equals(lockItem.state, CollabSyncGitUtility.RetainedLockState, StringComparison.Ordinal)
                    ? CollabSyncGitUtility.RetainedLockState
                    : "";
                lockItem.gitBranch ??= "";
                lockItem.gitHeadCommit ??= "";
                lockItem.gitProtectedBranch ??= "";
            }

            foreach (var item in doc.history)
            {
                item.userId ??= "";
                item.user ??= "";
            }
        }

        static bool TryParsePlainJsonDoc(string text, out CollabStateDocument doc)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                doc = new CollabStateDocument();
                Normalize(doc);
                return true;
            }

            try
            {
                doc = UnityEngine.JsonUtility.FromJson<CollabStateDocument>(text);
                if (doc == null)
                {
                    doc = null;
                    return false;
                }

                Normalize(doc);
                return true;
            }
            catch
            {
                doc = null;
                return false;
            }
        }

        static bool TryParsePlainJsonObject<T>(string text, out T value) where T : class
        {
            try
            {
                value = UnityEngine.JsonUtility.FromJson<T>(text);
                return value != null;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        static CollabStateDocument CloneDoc(CollabStateDocument doc)
        {
            Normalize(doc);
            var json = ToJson(doc);
            if (!TryParsePlainJsonDoc(json, out var clone))
                clone = new CollabStateDocument();
            Normalize(clone);
            return clone;
        }

        bool TryParseDoc(string storageText, out CollabStateDocument doc)
        {
            if (!CollabSyncProtectedStateUtility.TryReadSharedStateJson(storageText, _projectId, out var jsonText, out _))
            {
                doc = null;
                return false;
            }

            return TryParsePlainJsonDoc(jsonText, out doc);
        }

        static bool IsAutoLockReason(string reason)
        {
            return !string.IsNullOrEmpty(reason) && reason.StartsWith("auto-lock", StringComparison.Ordinal);
        }

        static bool IsUnlockRequestMemo(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   (text.StartsWith("[Unlock Request]", StringComparison.Ordinal)
                    || text.StartsWith("【解除依頼】", StringComparison.Ordinal));
        }

        static bool ContainsAdmin(CollabStateDocument doc, string userId, string userName, out int index)
        {
            Normalize(doc);

            for (int i = 0; i < doc.adminUsers.Count; i++)
            {
                var adminId = i < doc.adminUserIds.Count ? doc.adminUserIds[i] : "";
                var adminName = doc.adminUsers[i];
                if (CollabIdentityUtility.Matches(userId, userName, adminId, adminName))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        static bool ContainsBlocked(CollabStateDocument doc, string userId, string userName, out int index)
        {
            Normalize(doc);

            for (int i = 0; i < doc.blockedUsers.Count; i++)
            {
                var blockedId = i < doc.blockedUserIds.Count ? doc.blockedUserIds[i] : "";
                var blockedName = doc.blockedUsers[i];
                if (CollabIdentityUtility.Matches(userId, userName, blockedId, blockedName))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        static bool EnsureAdminBootstrap(CollabStateDocument doc, string actorId, string actorName)
        {
            Normalize(doc);

            actorId = CollabIdentityUtility.Normalize(actorId);
            actorName = CollabIdentityUtility.Normalize(actorName);
            if ((!string.IsNullOrEmpty(doc.rootAdminUserId) || !string.IsNullOrEmpty(doc.rootAdminUser)) || string.IsNullOrEmpty(actorId))
                return false;

            doc.rootAdminUserId = actorId;
            doc.rootAdminUser = actorName;
            doc.adminUserIds.Add(actorId);
            doc.adminUsers.Add(actorName);
            return true;
        }

        static void UpgradeAdminIdentityIfNeeded(CollabStateDocument doc, string userId, string userName)
        {
            Normalize(doc);

            if (ContainsAdmin(doc, userId, userName, out var index))
            {
                if (!string.IsNullOrEmpty(userId) && index < doc.adminUserIds.Count && !string.Equals(doc.adminUserIds[index] ?? "", userId, StringComparison.Ordinal))
                    doc.adminUserIds[index] = userId;
                if (!string.IsNullOrEmpty(userName) && !string.Equals(doc.adminUsers[index] ?? "", userName, StringComparison.Ordinal))
                    doc.adminUsers[index] = userName;
            }

            if (CollabIdentityUtility.Matches(userId, userName, doc.rootAdminUserId, doc.rootAdminUser))
            {
                if (!string.IsNullOrEmpty(userId) && !string.Equals(doc.rootAdminUserId ?? "", userId, StringComparison.Ordinal))
                    doc.rootAdminUserId = userId;
                if (!string.IsNullOrEmpty(userName) && !string.Equals(doc.rootAdminUser ?? "", userName, StringComparison.Ordinal))
                    doc.rootAdminUser = userName;
            }
        }

        static bool IsAdminUser(CollabStateDocument doc, string userId, string userName)
        {
            Normalize(doc);
            UpgradeAdminIdentityIfNeeded(doc, userId, userName);
            return ContainsAdmin(doc, userId, userName, out _);
        }

        static bool IsBlockedUser(CollabStateDocument doc, string userId, string userName)
        {
            Normalize(doc);
            return ContainsBlocked(doc, userId, userName, out _);
        }

        static bool IsRootAdminUser(CollabStateDocument doc, string userId, string userName)
        {
            Normalize(doc);
            UpgradeAdminIdentityIfNeeded(doc, userId, userName);
            return CollabIdentityUtility.Matches(userId, userName, doc.rootAdminUserId, doc.rootAdminUser);
        }

        static bool IsWorkHistoryEnabled(CollabStateDocument doc)
        {
            Normalize(doc);
            return !string.Equals(doc.workHistoryMode, "disabled", StringComparison.Ordinal);
        }

        static void AppendHistory(
            CollabStateDocument doc,
            string userId,
            string user,
            string action,
            string assetPath,
            string context = "",
            string detail = "",
            long createdAt = 0)
        {
            Normalize(doc);

            if ((string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(user)) || string.IsNullOrWhiteSpace(action))
                return;
            if (!IsWorkHistoryEnabled(doc))
                return;

            long timestamp = createdAt > 0 ? createdAt : TimeUtil.NowMs();
            var latest = doc.history.Count > 0 ? doc.history[0] : null;
            if (latest != null &&
                string.Equals(latest.userId ?? "", userId ?? "", StringComparison.Ordinal) &&
                string.Equals(latest.user, user, StringComparison.Ordinal) &&
                string.Equals(latest.action, action, StringComparison.Ordinal) &&
                string.Equals(latest.assetPath ?? "", assetPath ?? "", StringComparison.Ordinal) &&
                string.Equals(latest.context ?? "", context ?? "", StringComparison.Ordinal) &&
                string.Equals(latest.detail ?? "", detail ?? "", StringComparison.Ordinal) &&
                timestamp - latest.createdAt <= HistoryDuplicateWindowMs)
            {
                return;
            }

            doc.history.Insert(0, new WorkHistoryItem
            {
                id = Guid.NewGuid().ToString("N"),
                userId = userId ?? "",
                user = user,
                action = action,
                assetPath = assetPath ?? "",
                context = context ?? "",
                detail = detail ?? "",
                createdAt = timestamp
            });

            if (doc.history.Count > MaxHistoryItems)
                doc.history.RemoveRange(MaxHistoryItems, doc.history.Count - MaxHistoryItems);
        }

        void EnsureJsonFileExists()
        {
            EnsureStoreReady();
        }

        void EnsureStorageProtectionMode()
        {
            EnsureStoreReady();
            RewriteStoreProtectionIfNeeded();
        }

        void EnsureStoreReady()
        {
            var parentDir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            Directory.CreateDirectory(_storeDirectory);
            EnsureCategoryDirectories();

            if (TryReadStoreManifest(out _))
            {
                WriteStoreManifest();
                return;
            }

            if (StoreHasShardData())
            {
                WriteStoreManifest();
                return;
            }

            if (File.Exists(_path))
            {
                var existingText = SafeReadAllText(_path);
                if (!TryReadStoreManifest(existingText, out _) &&
                    TryParseDoc(existingText, out var legacyDoc))
                {
                    Normalize(legacyDoc);
                    if (legacyDoc.updatedAt <= 0)
                        legacyDoc.updatedAt = TimeUtil.NowMs();
                    WriteDocDelta(new CollabStateDocument(), legacyDoc);
                    if (!string.IsNullOrWhiteSpace(existingText))
                        WriteBackup(ToJson(legacyDoc));
                }
            }

            WriteStoreManifest();
        }

        void EnsureCategoryDirectories()
        {
            Directory.CreateDirectory(GetCategoryDirectory("state"));
            Directory.CreateDirectory(GetCategoryDirectory("presences"));
            Directory.CreateDirectory(GetCategoryDirectory("locks"));
            Directory.CreateDirectory(GetCategoryDirectory("memos"));
            Directory.CreateDirectory(GetCategoryDirectory("memo-reads"));
            Directory.CreateDirectory(GetCategoryDirectory("admins"));
            Directory.CreateDirectory(GetCategoryDirectory("blocked"));
            Directory.CreateDirectory(GetCategoryDirectory("history"));
        }

        void RewriteStoreProtectionIfNeeded()
        {
            if (!Directory.Exists(_storeDirectory))
                return;

            foreach (var file in Directory.GetFiles(_storeDirectory, "*.json", SearchOption.AllDirectories))
            {
                var currentText = SafeReadAllText(file);
                if (!TryDecodeStorageText(currentText, out var jsonText))
                    continue;

                var isProtected = CollabSyncProtectedStateUtility.IsProtectedPayload(currentText);
                if (isProtected == _protectSharedStateFile && !string.IsNullOrWhiteSpace(currentText))
                    continue;

                WriteAtomicText(file, EncodeStorageText(jsonText));
            }

            WriteStoreManifest();
        }

        string EncodeStorageText(string jsonText)
        {
            return CollabSyncProtectedStateUtility.EncodeSharedStateStorageText(jsonText, _projectId, _protectSharedStateFile);
        }

        bool TryDecodeStorageText(string storageText, out string jsonText)
        {
            if (CollabSyncProtectedStateUtility.TryReadSharedStateJson(storageText, _projectId, out jsonText, out _))
                return true;

            jsonText = null;
            return false;
        }

        bool TryReadStoreManifest(out ShardStoreManifest manifest)
        {
            manifest = null;
            if (!File.Exists(_storeManifestPath))
                return false;

            return TryReadStoreManifest(SafeReadAllText(_storeManifestPath), out manifest);
        }

        bool TryReadStoreManifest(string text, out ShardStoreManifest manifest)
        {
            if (!TryParsePlainJsonObject(text, out manifest))
                return false;

            return manifest != null
                && string.Equals(manifest.format ?? "", ShardManifestFormat, StringComparison.Ordinal);
        }

        void WriteStoreManifest()
        {
            var manifest = new ShardStoreManifest
            {
                format = ShardManifestFormat,
                storeDirectoryName = Path.GetFileName(_storeDirectory),
                protectSharedStateFile = _protectSharedStateFile,
                updatedAt = TimeUtil.NowMs()
            };
            WriteAtomicText(_storeManifestPath, ToJsonObject(manifest));
        }

        string SafeReadAllText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch
            {
                return "";
            }
        }

        void WriteAtomicText(string path, string text)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, text ?? "", Encoding.UTF8);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch
                    {
                        File.Copy(tempPath, path, true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch
            {
            }
        }

        string GetCategoryDirectory(string category)
        {
            return Path.Combine(_storeDirectory, category);
        }

        string GetRecordPath(string category, string key)
        {
            return Path.Combine(GetCategoryDirectory(category), ComputeRecordFileName(key));
        }

        static string ComputeRecordFileName(string key)
        {
            return UnityEngine.Hash128.Compute(key ?? "").ToString() + ".json";
        }

        static string BuildIdentityKey(string userId, string userName)
        {
            userId = CollabIdentityUtility.Normalize(userId);
            userName = CollabIdentityUtility.Normalize(userName);
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(userName))
                return "";
            return !string.IsNullOrEmpty(userId) ? "id:" + userId : "legacy:" + userName;
        }

        static string GetPresenceIdentityKey(EditingPresence presence)
        {
            return presence == null ? "" : BuildIdentityKey(presence.userId, presence.user);
        }

        static string GetMemoIdentityKey(MemoItem memo)
        {
            return memo?.id ?? "";
        }

        static string GetMemoReadIdentityKey(string memoId, string userId, string userName)
        {
            return (memoId ?? "") + "|" + BuildIdentityKey(userId, userName);
        }

        static string GetLockIdentityKey(LockItem item)
        {
            return item?.assetPath ?? "";
        }

        static string GetWorkHistoryIdentityKey(WorkHistoryItem item)
        {
            return item?.id ?? "";
        }

        static string BuildMemoBodySignature(MemoItem memo)
        {
            if (memo == null)
                return "";

            var comparable = new MemoItem
            {
                id = memo.id ?? "",
                authorId = memo.authorId ?? "",
                text = memo.text ?? "",
                author = memo.author ?? "",
                assetPath = memo.assetPath ?? "",
                createdAt = memo.createdAt,
                pinned = memo.pinned
            };
            return ToJsonObject(comparable);
        }

        static MemoItem CloneMemoForStorage(MemoItem memo)
        {
            if (memo == null)
                return new MemoItem();

            return new MemoItem
            {
                id = memo.id ?? "",
                authorId = memo.authorId ?? "",
                text = memo.text ?? "",
                author = memo.author ?? "",
                assetPath = memo.assetPath ?? "",
                createdAt = memo.createdAt,
                pinned = memo.pinned,
                readByUserIds = new List<string>(),
                readByUsers = new List<string>()
            };
        }

        static string BuildIdentitySignature(string userId, string userName)
        {
            return (userId ?? "") + "\n" + (userName ?? "");
        }

        bool StoreHasShardData()
        {
            try
            {
                return Directory.Exists(_storeDirectory)
                    && Directory.GetFiles(_storeDirectory, "*.json", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        CollabStateDocument ReadLatestBackup()
        {
            try
            {
                if (!Directory.Exists(_backupDirectory))
                    return new CollabStateDocument();

                foreach (var file in Directory.GetFiles(_backupDirectory, _backupPrefix + "*.json")
                                              .OrderByDescending(Path.GetFileName))
                {
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    if (TryParseDoc(text, out var backupDoc))
                        return backupDoc;
                }
            }
            catch
            {
            }

            return new CollabStateDocument();
        }

        CollabStateDocument ReadDoc()
        {
            EnsureJsonFileExists();

            if (TryReadDocFromShardStore(out var currentDoc))
                return currentDoc;

            return ReadLatestBackup();
        }

        bool TryReadStoreRecord<T>(string path, out T record) where T : class
        {
            record = null;
            var storageText = SafeReadAllText(path);
            if (string.IsNullOrWhiteSpace(storageText))
                return false;
            if (!TryDecodeStorageText(storageText, out var jsonText))
                return false;

            return TryParsePlainJsonObject(jsonText, out record);
        }

        IEnumerable<T> ReadAllStoreRecords<T>(string category) where T : class
        {
            var directory = GetCategoryDirectory(category);
            if (!Directory.Exists(directory))
                yield break;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                yield break;
            }

            foreach (var file in files)
            {
                if (TryReadStoreRecord<T>(file, out var record) && record != null)
                    yield return record;
            }
        }

        IEnumerable<T> ReadLatestStoreRecords<T>(string category, Func<T, string> keySelector, Func<T, long> updatedAtSelector) where T : class
        {
            var latestByKey = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var record in ReadAllStoreRecords<T>(category))
            {
                var key = keySelector(record) ?? "";
                if (string.IsNullOrEmpty(key))
                    continue;

                if (!latestByKey.TryGetValue(key, out var existing) || updatedAtSelector(record) >= updatedAtSelector(existing))
                    latestByKey[key] = record;
            }

            return latestByKey.Values;
        }

        bool TryReadDocFromShardStore(out CollabStateDocument doc)
        {
            doc = new CollabStateDocument();

            if (!StoreHasShardData())
            {
                Normalize(doc);
                return true;
            }

            long latestUpdatedAt = 0;

            if (TryReadStoreRecord<SharedStateRecord>(Path.Combine(GetCategoryDirectory("state"), "shared.json"), out var stateRecord) && stateRecord != null)
            {
                doc.rootAdminUserId = stateRecord.rootAdminUserId ?? "";
                doc.rootAdminUser = stateRecord.rootAdminUser ?? "";
                doc.workHistoryMode = stateRecord.workHistoryMode ?? "enabled";
                latestUpdatedAt = Math.Max(latestUpdatedAt, stateRecord.updatedAt);
            }

            foreach (var adminRecord in ReadLatestStoreRecords<IdentityRecord>("admins",
                         r => BuildIdentityKey(r.userId, r.userName),
                         r => r.updatedAt))
            {
                if (adminRecord == null || adminRecord.deleted)
                    continue;

                doc.adminUserIds.Add(adminRecord.userId ?? "");
                doc.adminUsers.Add(adminRecord.userName ?? "");
                latestUpdatedAt = Math.Max(latestUpdatedAt, adminRecord.updatedAt);
            }

            foreach (var blockedRecord in ReadLatestStoreRecords<IdentityRecord>("blocked",
                         r => BuildIdentityKey(r.userId, r.userName),
                         r => r.updatedAt))
            {
                if (blockedRecord == null || blockedRecord.deleted)
                    continue;

                doc.blockedUserIds.Add(blockedRecord.userId ?? "");
                doc.blockedUsers.Add(blockedRecord.userName ?? "");
                latestUpdatedAt = Math.Max(latestUpdatedAt, blockedRecord.updatedAt);
            }

            foreach (var presenceRecord in ReadLatestStoreRecords<PresenceRecord>("presences",
                         r => GetPresenceIdentityKey(r.presence),
                         r => r.updatedAt))
            {
                if (presenceRecord?.presence == null || presenceRecord.deleted)
                    continue;

                doc.presences.Add(presenceRecord.presence);
                latestUpdatedAt = Math.Max(latestUpdatedAt, presenceRecord.updatedAt);
            }

            foreach (var lockRecord in ReadLatestStoreRecords<LockRecord>("locks",
                         r => GetLockIdentityKey(r.item),
                         r => r.updatedAt))
            {
                if (lockRecord?.item == null || lockRecord.deleted)
                    continue;

                doc.locks.Add(lockRecord.item);
                latestUpdatedAt = Math.Max(latestUpdatedAt, lockRecord.updatedAt);
            }

            var memosById = new Dictionary<string, MemoItem>(StringComparer.Ordinal);
            foreach (var memoRecord in ReadLatestStoreRecords<MemoRecord>("memos",
                         r => GetMemoIdentityKey(r.item),
                         r => r.updatedAt))
            {
                if (memoRecord?.item == null || memoRecord.deleted || string.IsNullOrEmpty(memoRecord.item.id))
                    continue;

                var memo = memoRecord.item;
                memo.readByUserIds ??= new List<string>();
                memo.readByUsers ??= new List<string>();
                memosById[memo.id] = memo;
                latestUpdatedAt = Math.Max(latestUpdatedAt, memoRecord.updatedAt);
            }

            foreach (var readRecord in ReadLatestStoreRecords<MemoReadRecord>("memo-reads",
                         r => GetMemoReadIdentityKey(r.memoId, r.userId, r.userName),
                         r => r.updatedAt))
            {
                if (readRecord == null || readRecord.deleted)
                    continue;
                if (!memosById.TryGetValue(readRecord.memoId ?? "", out var memo))
                    continue;

                CollabIdentityUtility.AddReadMarker(memo, readRecord.userId ?? "", readRecord.userName ?? "");
                latestUpdatedAt = Math.Max(latestUpdatedAt, readRecord.updatedAt);
            }

            doc.memos = memosById.Values
                .OrderByDescending(m => m.pinned)
                .ThenByDescending(m => m.createdAt)
                .ToList();

            doc.history = ReadAllStoreRecords<HistoryRecord>("history")
                .Where(record => record?.item != null && !string.IsNullOrEmpty(record.item.id))
                .OrderByDescending(record => record.item.createdAt)
                .ThenByDescending(record => record.updatedAt)
                .Take(MaxHistoryItems)
                .Select(record =>
                {
                    latestUpdatedAt = Math.Max(latestUpdatedAt, record.updatedAt);
                    return record.item;
                })
                .ToList();

            doc.updatedAt = latestUpdatedAt;
            Normalize(doc);
            return true;
        }

        void WriteStoreRecord<T>(string path, T record) where T : class
        {
            if (record == null)
                return;

            WriteAtomicText(path, EncodeStorageText(ToJsonObject(record)));
        }

        void WriteDocDelta(CollabStateDocument previousDoc, CollabStateDocument nextDoc)
        {
            EnsureCategoryDirectories();
            SyncSharedStateRecord(previousDoc, nextDoc);
            SyncIdentityRecords("admins", previousDoc.adminUserIds, previousDoc.adminUsers, nextDoc.adminUserIds, nextDoc.adminUsers, nextDoc.updatedAt);
            SyncIdentityRecords("blocked", previousDoc.blockedUserIds, previousDoc.blockedUsers, nextDoc.blockedUserIds, nextDoc.blockedUsers, nextDoc.updatedAt);
            SyncPresenceRecords(previousDoc.presences, nextDoc.presences, nextDoc.updatedAt);
            SyncLockRecords(previousDoc.locks, nextDoc.locks, nextDoc.updatedAt);
            SyncMemoRecords(previousDoc.memos, nextDoc.memos, nextDoc.updatedAt);
            SyncHistoryRecords(previousDoc.history, nextDoc.history, nextDoc.updatedAt);
        }

        void SyncSharedStateRecord(CollabStateDocument previousDoc, CollabStateDocument nextDoc)
        {
            if (string.Equals(previousDoc.rootAdminUserId ?? "", nextDoc.rootAdminUserId ?? "", StringComparison.Ordinal)
                && string.Equals(previousDoc.rootAdminUser ?? "", nextDoc.rootAdminUser ?? "", StringComparison.Ordinal)
                && string.Equals(previousDoc.workHistoryMode ?? "enabled", nextDoc.workHistoryMode ?? "enabled", StringComparison.Ordinal))
            {
                return;
            }

            WriteStoreRecord(
                Path.Combine(GetCategoryDirectory("state"), "shared.json"),
                new SharedStateRecord
                {
                    rootAdminUserId = nextDoc.rootAdminUserId ?? "",
                    rootAdminUser = nextDoc.rootAdminUser ?? "",
                    workHistoryMode = nextDoc.workHistoryMode ?? "enabled",
                    updatedAt = nextDoc.updatedAt
                });
        }

        void SyncIdentityRecords(string category, List<string> previousIds, List<string> previousNames, List<string> nextIds, List<string> nextNames, long updatedAt)
        {
            var previous = BuildIdentityDictionary(previousIds, previousNames);
            var next = BuildIdentityDictionary(nextIds, nextNames);

            foreach (var pair in next)
            {
                var nextRecord = pair.Value;
                if (!previous.TryGetValue(pair.Key, out var previousRecord)
                    || !string.Equals(BuildIdentitySignature(previousRecord.userId, previousRecord.userName), BuildIdentitySignature(nextRecord.userId, nextRecord.userName), StringComparison.Ordinal))
                {
                    nextRecord.updatedAt = updatedAt;
                    nextRecord.deleted = false;
                    WriteStoreRecord(GetRecordPath(category, pair.Key), nextRecord);
                }
            }

            foreach (var pair in previous)
            {
                if (next.ContainsKey(pair.Key))
                    continue;

                WriteStoreRecord(
                    GetRecordPath(category, pair.Key),
                    new IdentityRecord
                    {
                        userId = pair.Value.userId ?? "",
                        userName = pair.Value.userName ?? "",
                        updatedAt = updatedAt,
                        deleted = true
                    });
            }
        }

        Dictionary<string, IdentityRecord> BuildIdentityDictionary(List<string> ids, List<string> names)
        {
            var result = new Dictionary<string, IdentityRecord>(StringComparer.Ordinal);
            var count = Math.Max(ids?.Count ?? 0, names?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                var userId = i < (ids?.Count ?? 0) ? ids[i] ?? "" : "";
                var userName = i < (names?.Count ?? 0) ? names[i] ?? "" : "";
                var key = BuildIdentityKey(userId, userName);
                if (string.IsNullOrEmpty(key))
                    continue;

                result[key] = new IdentityRecord
                {
                    userId = userId,
                    userName = userName
                };
            }

            return result;
        }

        void SyncPresenceRecords(List<EditingPresence> previousPresences, List<EditingPresence> nextPresences, long updatedAt)
        {
            var previous = (previousPresences ?? new List<EditingPresence>())
                .Where(p => p != null && !string.IsNullOrEmpty(GetPresenceIdentityKey(p)))
                .ToDictionary(GetPresenceIdentityKey, p => p, StringComparer.Ordinal);
            var next = (nextPresences ?? new List<EditingPresence>())
                .Where(p => p != null && !string.IsNullOrEmpty(GetPresenceIdentityKey(p)))
                .ToDictionary(GetPresenceIdentityKey, p => p, StringComparer.Ordinal);

            foreach (var pair in next)
            {
                if (!previous.TryGetValue(pair.Key, out var previousPresence)
                    || !string.Equals(ToJsonObject(previousPresence), ToJsonObject(pair.Value), StringComparison.Ordinal))
                {
                    WriteStoreRecord(
                        GetRecordPath("presences", pair.Key),
                        new PresenceRecord
                        {
                            presence = pair.Value,
                            updatedAt = updatedAt,
                            deleted = false
                        });
                }
            }

            foreach (var pair in previous)
            {
                if (next.ContainsKey(pair.Key))
                    continue;

                WriteStoreRecord(
                    GetRecordPath("presences", pair.Key),
                    new PresenceRecord
                    {
                        presence = pair.Value,
                        updatedAt = updatedAt,
                        deleted = true
                    });
            }
        }

        void SyncLockRecords(List<LockItem> previousLocks, List<LockItem> nextLocks, long updatedAt)
        {
            var previous = (previousLocks ?? new List<LockItem>())
                .Where(item => item != null && !string.IsNullOrEmpty(GetLockIdentityKey(item)))
                .ToDictionary(GetLockIdentityKey, item => item, StringComparer.Ordinal);
            var next = (nextLocks ?? new List<LockItem>())
                .Where(item => item != null && !string.IsNullOrEmpty(GetLockIdentityKey(item)))
                .ToDictionary(GetLockIdentityKey, item => item, StringComparer.Ordinal);

            foreach (var pair in next)
            {
                if (!previous.TryGetValue(pair.Key, out var previousItem)
                    || !string.Equals(ToJsonObject(previousItem), ToJsonObject(pair.Value), StringComparison.Ordinal))
                {
                    WriteStoreRecord(
                        GetRecordPath("locks", pair.Key),
                        new LockRecord
                        {
                            item = pair.Value,
                            updatedAt = updatedAt,
                            deleted = false
                        });
                }
            }

            foreach (var pair in previous)
            {
                if (next.ContainsKey(pair.Key))
                    continue;

                WriteStoreRecord(
                    GetRecordPath("locks", pair.Key),
                    new LockRecord
                    {
                        item = pair.Value,
                        updatedAt = updatedAt,
                        deleted = true
                    });
            }
        }

        void SyncMemoRecords(List<MemoItem> previousMemos, List<MemoItem> nextMemos, long updatedAt)
        {
            var previous = (previousMemos ?? new List<MemoItem>())
                .Where(m => m != null && !string.IsNullOrEmpty(m.id))
                .ToDictionary(m => m.id, m => m, StringComparer.Ordinal);
            var next = (nextMemos ?? new List<MemoItem>())
                .Where(m => m != null && !string.IsNullOrEmpty(m.id))
                .ToDictionary(m => m.id, m => m, StringComparer.Ordinal);

            foreach (var pair in next)
            {
                if (!previous.TryGetValue(pair.Key, out var previousMemo)
                    || !string.Equals(BuildMemoBodySignature(previousMemo), BuildMemoBodySignature(pair.Value), StringComparison.Ordinal))
                {
                    WriteStoreRecord(
                        GetRecordPath("memos", pair.Key),
                        new MemoRecord
                        {
                            item = CloneMemoForStorage(pair.Value),
                            updatedAt = updatedAt,
                            deleted = false
                        });
                }

                var previousReads = new HashSet<string>(
                    (previousMemo?.readByUserIds ?? new List<string>())
                        .Select(userId => GetMemoReadIdentityKey(pair.Key, userId, ""))
                        .Concat((previousMemo?.readByUsers ?? new List<string>()).Select(userName => GetMemoReadIdentityKey(pair.Key, "", userName))),
                    StringComparer.Ordinal);
                var nextReadRecords = BuildMemoReadRecords(pair.Value, updatedAt);
                foreach (var readPair in nextReadRecords)
                {
                    if (previousReads.Contains(readPair.Key))
                        continue;

                    WriteStoreRecord(GetRecordPath("memo-reads", readPair.Key), readPair.Value);
                }
            }

            foreach (var pair in previous)
            {
                if (next.ContainsKey(pair.Key))
                    continue;

                WriteStoreRecord(
                    GetRecordPath("memos", pair.Key),
                    new MemoRecord
                    {
                        item = CloneMemoForStorage(pair.Value),
                        updatedAt = updatedAt,
                        deleted = true
                    });
            }
        }

        Dictionary<string, MemoReadRecord> BuildMemoReadRecords(MemoItem memo, long updatedAt)
        {
            var records = new Dictionary<string, MemoReadRecord>(StringComparer.Ordinal);
            if (memo == null || string.IsNullOrEmpty(memo.id))
                return records;

            CollabIdentityUtility.EnsureReadBy(memo);
            foreach (var userId in memo.readByUserIds ?? new List<string>())
            {
                var key = GetMemoReadIdentityKey(memo.id, userId, "");
                records[key] = new MemoReadRecord
                {
                    memoId = memo.id,
                    userId = userId ?? "",
                    userName = "",
                    updatedAt = updatedAt,
                    deleted = false
                };
            }

            foreach (var userName in memo.readByUsers ?? new List<string>())
            {
                var key = GetMemoReadIdentityKey(memo.id, "", userName);
                if (records.ContainsKey(key))
                    continue;

                records[key] = new MemoReadRecord
                {
                    memoId = memo.id,
                    userId = "",
                    userName = userName ?? "",
                    updatedAt = updatedAt,
                    deleted = false
                };
            }

            return records;
        }

        void SyncHistoryRecords(List<WorkHistoryItem> previousHistory, List<WorkHistoryItem> nextHistory, long updatedAt)
        {
            var existingIds = new HashSet<string>(
                (previousHistory ?? new List<WorkHistoryItem>())
                    .Where(item => item != null && !string.IsNullOrEmpty(item.id))
                    .Select(item => item.id),
                StringComparer.Ordinal);

            foreach (var item in nextHistory ?? new List<WorkHistoryItem>())
            {
                if (item == null || string.IsNullOrEmpty(item.id) || existingIds.Contains(item.id))
                    continue;

                WriteStoreRecord(
                    GetRecordPath("history", GetWorkHistoryIdentityKey(item)),
                    new HistoryRecord
                    {
                        item = item,
                        updatedAt = updatedAt
                    });
            }
        }

        void CreateBackupIfNeeded(CollabStateDocument previousDoc, CollabStateDocument nextDoc)
        {
            if (!HasMeaningfulChange(previousDoc, nextDoc))
                return;

            if (!ShouldCreateTimedBackup())
                return;

            WriteBackup(ToJson(previousDoc));
        }

        bool HasMeaningfulChange(CollabStateDocument previousDoc, CollabStateDocument nextDoc)
        {
            return BuildBackupSignature(previousDoc) != BuildBackupSignature(nextDoc);
        }

        bool HasDocumentChanged(CollabStateDocument previousDoc, CollabStateDocument nextDoc)
        {
            var previousComparable = CloneDoc(previousDoc);
            var nextComparable = CloneDoc(nextDoc);
            previousComparable.updatedAt = 0;
            nextComparable.updatedAt = 0;
            return !string.Equals(ToJson(previousComparable), ToJson(nextComparable), StringComparison.Ordinal);
        }

        string BuildBackupSignature(CollabStateDocument doc)
        {
            Normalize(doc);

            var builder = new StringBuilder();

            foreach (var memo in doc.memos.OrderBy(m => m.id ?? ""))
            {
                builder.Append("memo|")
                    .Append(memo.id).Append('|')
                    .Append(memo.authorId).Append('|')
                    .Append(memo.author).Append('|')
                    .Append(memo.assetPath).Append('|')
                    .Append(memo.createdAt).Append('|')
                    .Append(memo.pinned ? '1' : '0').Append('|')
                    .Append(memo.text).Append('|')
                    .Append(string.Join(",", (memo.readByUserIds ?? new List<string>()).OrderBy(x => x))).Append('|')
                    .Append(string.Join(",", (memo.readByUsers ?? new List<string>()).OrderBy(x => x)))
                    .AppendLine();
            }

            foreach (var lockItem in doc.locks.OrderBy(l => l.assetPath ?? "").ThenBy(l => l.ownerId ?? "").ThenBy(l => l.owner ?? ""))
            {
                builder.Append("lock|")
                    .Append(lockItem.assetPath).Append('|')
                    .Append(lockItem.scopeAssetPath).Append('|')
                    .Append(lockItem.ownerId).Append('|')
                    .Append(lockItem.owner).Append('|')
                    .Append(lockItem.reason).Append('|')
                    .Append(lockItem.createdAt).Append('|')
                    .Append(lockItem.ttlMs).Append('|')
                    .Append(lockItem.state).Append('|')
                    .Append(lockItem.retainedAt).Append('|')
                    .Append(lockItem.gitBranch).Append('|')
                    .Append(lockItem.gitHeadCommit).Append('|')
                    .Append(lockItem.gitProtectedBranch)
                    .AppendLine();
            }

            for (int i = 0; i < doc.adminUsers.Count; i++)
            {
                builder.Append("admin|")
                    .Append(i < doc.adminUserIds.Count ? doc.adminUserIds[i] : "").Append('|')
                    .Append(doc.adminUsers[i] ?? "")
                    .AppendLine();
            }

            for (int i = 0; i < doc.blockedUsers.Count; i++)
            {
                builder.Append("blocked|")
                    .Append(i < doc.blockedUserIds.Count ? doc.blockedUserIds[i] : "").Append('|')
                    .Append(doc.blockedUsers[i] ?? "")
                    .AppendLine();
            }

            builder.Append("root-admin|")
                .Append(doc.rootAdminUserId ?? "").Append('|')
                .Append(doc.rootAdminUser ?? "")
                .AppendLine();

            builder.Append("history-mode|")
                .Append(doc.workHistoryMode ?? "enabled")
                .AppendLine();

            return builder.ToString();
        }

        bool ShouldCreateTimedBackup()
        {
            try
            {
                if (!Directory.Exists(_backupDirectory))
                    return true;

                var latest = Directory.GetFiles(_backupDirectory, _backupPrefix + "*.json")
                                      .OrderByDescending(Path.GetFileName)
                                      .FirstOrDefault();
                if (string.IsNullOrEmpty(latest))
                    return true;

                var lastWriteUtc = File.GetLastWriteTimeUtc(latest);
                return DateTime.UtcNow - lastWriteUtc >= BackupMinInterval;
            }
            catch
            {
                return true;
            }
        }

        void WriteBackup(string text)
        {
            try
            {
                Directory.CreateDirectory(_backupDirectory);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                var filename = _backupPrefix + timestamp + ".json";
                var fullPath = Path.Combine(_backupDirectory, filename);
                File.WriteAllText(fullPath, text, Encoding.UTF8);
                PruneBackups();
            }
            catch
            {
            }
        }

        void PruneBackups()
        {
            try
            {
                if (!Directory.Exists(_backupDirectory))
                    return;

                var backups = Directory.GetFiles(_backupDirectory, _backupPrefix + "*.json")
                                       .OrderByDescending(Path.GetFileName)
                                       .ToArray();

                for (int i = MaxBackupFiles; i < backups.Length; i++)
                    File.Delete(backups[i]);
            }
            catch
            {
            }
        }

        bool MutateDocument(Func<CollabStateDocument, bool> mutate)
        {
            EnsureJsonFileExists();

            lock (_mutationLock)
            {
                var baseDoc = ReadDoc() ?? new CollabStateDocument();
                var previousDoc = CloneDoc(baseDoc);
                var workingDoc = CloneDoc(baseDoc);

                if (!mutate(workingDoc))
                    return false;

                Normalize(workingDoc);
                if (!HasDocumentChanged(previousDoc, workingDoc))
                    return false;

                workingDoc.updatedAt = TimeUtil.NowMs();

                CreateBackupIfNeeded(previousDoc, workingDoc);
                WriteDocDelta(previousDoc, workingDoc);
                return true;
            }
        }

        void Raise()
        {
            if (_disposed) return;

            try
            {
                var doc = ReadDoc();
                if (doc != null && doc.updatedAt > 0 && doc.updatedAt == _lastRaisedUpdatedAt)
                    return;

                _lastRaisedUpdatedAt = doc?.updatedAt ?? long.MinValue;
                _onUpdate?.Invoke(doc);
                CollabSyncEvents.RaiseDocUpdate(doc);
            }
            catch
            {
            }
        }

        void RequestRaise(bool immediate = false)
        {
            if (_disposed)
                return;

            var version = Interlocked.Increment(ref _raiseVersion);
            if (immediate)
            {
                RaiseIfLatest(version);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RaiseDebounceDelayMs).ConfigureAwait(false);
                    RaiseIfLatest(version);
                }
                catch
                {
                }
            });
        }

        void RaiseIfLatest(int version)
        {
            if (_disposed || version != Volatile.Read(ref _raiseVersion))
                return;

            Raise();
        }

        public void Subscribe(Action<CollabStateDocument> onUpdate)
        {
            _onUpdate = onUpdate;
            RequestRaise(immediate: true);
        }

        public Task<CollabStateDocument> LoadOnceAsync() => Task.FromResult(ReadDoc());

        public async Task PublishPresenceAsync(EditingPresence presence)
        {
            await Task.Yield();

            if (presence == null) return;

            if (MutateDocument(doc =>
            {
                presence.userId = CollabIdentityUtility.Normalize(presence.userId);
                presence.user = CollabIdentityUtility.Normalize(presence.user);
                if (IsBlockedUser(doc, presence.userId, presence.user))
                    return false;
                EnsureAdminBootstrap(doc, presence.userId, presence.user);
                UpgradeAdminIdentityIfNeeded(doc, presence.userId, presence.user);

                var previous = doc.presences.FirstOrDefault(p => CollabIdentityUtility.Matches(presence.userId, presence.user, p.userId, p.user));
                doc.presences.RemoveAll(p => CollabIdentityUtility.Matches(presence.userId, presence.user, p.userId, p.user));
                doc.presences.Add(presence);

                bool changedTarget =
                    previous == null ||
                    !string.Equals(previous.assetPath ?? "", presence.assetPath ?? "", StringComparison.Ordinal) ||
                    !string.Equals(previous.targetKey ?? "", presence.targetKey ?? "", StringComparison.Ordinal) ||
                    !string.Equals(previous.context ?? "", presence.context ?? "", StringComparison.Ordinal);

                if (changedTarget && !string.IsNullOrEmpty(presence.assetPath))
                {
                    AppendHistory(
                        doc,
                        presence.userId,
                        presence.user,
                        "editing",
                        presence.assetPath,
                        presence.context,
                        presence.context,
                        presence.heartbeat);
                }

                return true;
            }))
            {
                Raise();
            }
        }

        public async Task UpsertMemoAsync(MemoItem memo)
        {
            await Task.Yield();

            if (memo == null) return;

            if (MutateDocument(doc =>
            {
                memo.authorId = CollabIdentityUtility.Normalize(memo.authorId);
                memo.author = CollabIdentityUtility.Normalize(memo.author);
                if (IsBlockedUser(doc, memo.authorId, memo.author))
                    return false;
                EnsureAdminBootstrap(doc, memo.authorId, memo.author);
                UpgradeAdminIdentityIfNeeded(doc, memo.authorId, memo.author);
                CollabIdentityUtility.EnsureReadBy(memo);

                var index = doc.memos.FindIndex(m => m.id == memo.id);
                if (index >= 0) doc.memos[index] = memo;
                else
                {
                    doc.memos.Add(memo);
                    AppendHistory(
                        doc,
                        memo.authorId,
                        memo.author,
                        IsUnlockRequestMemo(memo.text) ? "unlock-request" : "memo",
                        memo.assetPath,
                        "",
                        (memo.text ?? "").Trim(),
                        memo.createdAt);
                }

                doc.memos = doc.memos
                    .OrderByDescending(m => m.pinned)
                    .ThenByDescending(m => m.createdAt)
                    .ToList();

                return true;
            }))
            {
                Raise();
            }
        }

        public async Task MarkMemoReadAsync(string memoId, string userId, string userName)
        {
            await Task.Yield();

            userId = CollabIdentityUtility.Normalize(userId);
            userName = CollabIdentityUtility.Normalize(userName);
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(userName)) return;

            if (MutateDocument(doc =>
            {
                if (IsBlockedUser(doc, userId, userName))
                    return false;
                EnsureAdminBootstrap(doc, userId, userName);
                UpgradeAdminIdentityIfNeeded(doc, userId, userName);

                var memo = doc.memos.FirstOrDefault(m => m.id == memoId);
                if (memo == null) return false;

                return CollabIdentityUtility.AddReadMarker(memo, userId, userName);
            }))
            {
                Raise();
            }
        }

        public async Task<bool> DeleteMemoAsync(string memoId, string requesterId, string requesterName)
        {
            await Task.Yield();

            bool deleted = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                UpgradeAdminIdentityIfNeeded(doc, requesterId, requesterName);

                var memo = doc.memos.FirstOrDefault(m => m.id == memoId);
                if (memo == null || !CollabIdentityUtility.Matches(requesterId, requesterName, memo.authorId, memo.author))
                    return false;

                int removed = doc.memos.RemoveAll(m => m.id == memoId);
                deleted = removed > 0;
                if (deleted)
                    AppendHistory(doc, requesterId, requesterName, "memo-delete", memo.assetPath, "", (memo.text ?? "").Trim(), TimeUtil.NowMs());
                return deleted;
            }))
            {
                Raise();
            }

            return deleted;
        }

        public async Task<bool> ForceDeleteMemoAsync(string memoId, string requesterId, string requesterName)
        {
            await Task.Yield();

            bool deleted = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsAdminUser(doc, requesterId, requesterName))
                    return false;

                var memo = doc.memos.FirstOrDefault(m => m.id == memoId);
                if (memo == null)
                    return false;

                int removed = doc.memos.RemoveAll(m => m.id == memoId);
                deleted = removed > 0;
                if (deleted)
                    AppendHistory(doc, requesterId, requesterName, "memo-force-delete", memo.assetPath, "", CollabIdentityUtility.DisplayName(memo.authorId, memo.author), TimeUtil.NowMs());
                return deleted;
            }))
            {
                Raise();
            }

            return deleted;
        }

        public async Task<bool> TryAcquireLockAsync(string assetPath, string ownerId, string ownerName, string reason = "", long ttlMs = 0, string scopeAssetPath = "")
        {
            await Task.Yield();

            bool acquired = false;
            bool requestedAutoLock = IsAutoLockReason(reason);
            bool gitAwareRetainedLocksEnabled = CollabSyncConfig.IsGitAwareRetainedLocksEnabled();
            var gitSnapshot = default(GitLockSnapshot);
            if (gitAwareRetainedLocksEnabled)
                CollabSyncGitUtility.TryCaptureCurrentLockSnapshot(out gitSnapshot);
            if (MutateDocument(doc =>
            {
                assetPath = (assetPath ?? "").Replace('\\', '/');
                ownerId = CollabIdentityUtility.Normalize(ownerId);
                ownerName = CollabIdentityUtility.Normalize(ownerName);
                scopeAssetPath = (scopeAssetPath ?? "").Replace('\\', '/');
                if (string.IsNullOrEmpty(scopeAssetPath) && !string.IsNullOrEmpty(assetPath))
                    scopeAssetPath = assetPath;
                if (IsBlockedUser(doc, ownerId, ownerName))
                    return false;
                EnsureAdminBootstrap(doc, ownerId, ownerName);
                UpgradeAdminIdentityIfNeeded(doc, ownerId, ownerName);

                var now = TimeUtil.NowMs();
                doc.locks.RemoveAll(l => l.ttlMs > 0 && now - l.createdAt > l.ttlMs);

                var existing = doc.locks.FirstOrDefault(l => l.assetPath == assetPath);
                if (existing != null && !CollabIdentityUtility.Matches(ownerId, ownerName, existing.ownerId, existing.owner))
                {
                    acquired = false;
                    return false;
                }

                if (existing == null)
                {
                    var lockItem = new LockItem
                    {
                        assetPath = assetPath,
                        scopeAssetPath = scopeAssetPath,
                        ownerId = ownerId,
                        owner = ownerName,
                        reason = reason,
                        createdAt = now,
                        ttlMs = ttlMs
                    };
                    if (gitAwareRetainedLocksEnabled)
                        CollabSyncGitUtility.ApplySnapshot(lockItem, gitSnapshot, retained: false);
                    doc.locks.Add(lockItem);
                    AppendHistory(doc, ownerId, ownerName, "lock", assetPath, "", reason, now);
                }
                else
                {
                    var previousReason = existing.reason;
                    var wasRetained = CollabSyncGitUtility.IsRetainedLock(existing);
                    if (requestedAutoLock && !IsAutoLockReason(existing.reason))
                    {
                        acquired = true;
                        return false;
                    }

                    existing.ownerId = ownerId;
                    existing.owner = ownerName;
                    if (!string.IsNullOrEmpty(scopeAssetPath))
                        existing.scopeAssetPath = scopeAssetPath;
                    existing.reason = reason;
                    existing.createdAt = now;
                    existing.ttlMs = ttlMs;

                    bool shouldPreserveManualGitBaseline =
                        gitAwareRetainedLocksEnabled &&
                        !wasRetained &&
                        !requestedAutoLock &&
                        ttlMs <= 0 &&
                        !string.IsNullOrEmpty(existing.gitHeadCommit);

                    if (!shouldPreserveManualGitBaseline)
                        CollabSyncGitUtility.ApplySnapshot(existing, gitSnapshot, retained: false);
                    else
                    {
                        existing.state = "";
                        existing.retainedAt = 0;
                    }

                    if (!requestedAutoLock && !string.Equals(previousReason ?? "", reason ?? "", StringComparison.Ordinal))
                        AppendHistory(doc, ownerId, ownerName, "lock", assetPath, "", reason, now);
                }

                acquired = true;
                return true;
            }))
            {
                Raise();
            }

            return acquired;
        }

        public async Task<bool> ReleaseLockAsync(string assetPath, string ownerId, string ownerName)
        {
            await Task.Yield();

            bool released = false;
            bool gitAwareRetainedLocksEnabled = CollabSyncConfig.IsGitAwareRetainedLocksEnabled();
            var gitSnapshot = default(GitLockSnapshot);
            if (gitAwareRetainedLocksEnabled)
                CollabSyncGitUtility.TryCaptureCurrentLockSnapshot(out gitSnapshot);
            if (MutateDocument(doc =>
            {
                ownerId = CollabIdentityUtility.Normalize(ownerId);
                ownerName = CollabIdentityUtility.Normalize(ownerName);
                if (IsBlockedUser(doc, ownerId, ownerName))
                    return false;
                EnsureAdminBootstrap(doc, ownerId, ownerName);
                UpgradeAdminIdentityIfNeeded(doc, ownerId, ownerName);

                var existing = doc.locks.FirstOrDefault(l =>
                    string.Equals(l.assetPath, assetPath, StringComparison.Ordinal) &&
                    CollabIdentityUtility.Matches(ownerId, ownerName, l.ownerId, l.owner));
                if (existing == null)
                    return false;

                var now = TimeUtil.NowMs();
                if (gitAwareRetainedLocksEnabled && CollabSyncGitUtility.IsRetainedLock(existing))
                {
                    if (!CollabSyncGitUtility.CanReleaseRetainedLock(existing))
                    {
                        released = false;
                        return false;
                    }

                    int removedRetained = doc.locks.RemoveAll(l =>
                        string.Equals(l.assetPath, assetPath, StringComparison.Ordinal) &&
                        CollabIdentityUtility.Matches(ownerId, ownerName, l.ownerId, l.owner));
                    released = removedRetained > 0;
                    if (released)
                        AppendHistory(doc, ownerId, ownerName, "unlock", assetPath, "", existing.reason, now);
                    return released;
                }

                if (gitAwareRetainedLocksEnabled && CollabSyncGitUtility.ShouldRetainLockOnRelease(existing, gitSnapshot))
                {
                    CollabSyncGitUtility.ApplySnapshot(existing, gitSnapshot, retained: true);
                    released = true;
                    AppendHistory(doc, ownerId, ownerName, "lock-retained", assetPath, "", CollabSyncGitUtility.BuildRetainedHistoryDetail(gitSnapshot), now);
                    return true;
                }

                int removed = doc.locks.RemoveAll(l =>
                    string.Equals(l.assetPath, assetPath, StringComparison.Ordinal) &&
                    CollabIdentityUtility.Matches(ownerId, ownerName, l.ownerId, l.owner));
                released = removed > 0;
                if (released)
                    AppendHistory(doc, ownerId, ownerName, "unlock", assetPath, "", existing.reason, now);
                return released;
            }))
            {
                Raise();
            }

            return released;
        }

        public async Task<bool> ForceReleaseLockAsync(string assetPath, string requesterId, string requesterName)
        {
            await Task.Yield();

            bool released = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsAdminUser(doc, requesterId, requesterName))
                    return false;

                var existing = doc.locks.FirstOrDefault(l => l.assetPath == assetPath);
                if (existing == null)
                    return false;

                int removed = doc.locks.RemoveAll(l => l.assetPath == assetPath);
                released = removed > 0;
                if (released)
                    AppendHistory(doc, requesterId, requesterName, "force-unlock", assetPath, "", CollabIdentityUtility.DisplayName(existing.ownerId, existing.owner), TimeUtil.NowMs());
                return released;
            }))
            {
                Raise();
            }

            return released;
        }

        public async Task<bool> AddAdminAsync(string requesterId, string requesterName, string adminUserId, string adminUserName)
        {
            await Task.Yield();

            bool changed = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                adminUserId = CollabIdentityUtility.Normalize(adminUserId);
                adminUserName = CollabIdentityUtility.Normalize(adminUserName);
                if (IsBlockedUser(doc, requesterId, requesterName) || IsBlockedUser(doc, adminUserId, adminUserName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsRootAdminUser(doc, requesterId, requesterName))
                    return false;

                if (string.IsNullOrEmpty(adminUserId))
                    return false;

                if (ContainsAdmin(doc, adminUserId, adminUserName, out var index))
                {
                    if (index < doc.adminUsers.Count && !string.IsNullOrEmpty(adminUserName) && !string.Equals(doc.adminUsers[index] ?? "", adminUserName, StringComparison.Ordinal))
                        doc.adminUsers[index] = adminUserName;
                    return false;
                }

                doc.adminUserIds.Add(adminUserId);
                doc.adminUsers.Add(string.IsNullOrEmpty(adminUserName) ? adminUserId : adminUserName);
                AppendHistory(doc, requesterId, requesterName, "admin-grant", "", "", string.IsNullOrEmpty(adminUserName) ? adminUserId : adminUserName, TimeUtil.NowMs());
                changed = true;
                return true;
            }))
            {
                Raise();
            }

            return changed;
        }

        public async Task<bool> RemoveAdminAsync(string requesterId, string requesterName, string adminUserId)
        {
            await Task.Yield();

            bool changed = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                adminUserId = CollabIdentityUtility.Normalize(adminUserId);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsRootAdminUser(doc, requesterId, requesterName))
                    return false;

                if (string.IsNullOrEmpty(adminUserId) || CollabIdentityUtility.Matches(adminUserId, "", doc.rootAdminUserId, doc.rootAdminUser))
                    return false;

                for (int i = 0; i < doc.adminUsers.Count; i++)
                {
                    var existingId = i < doc.adminUserIds.Count ? doc.adminUserIds[i] : "";
                    var existingName = doc.adminUsers[i];
                    if (!CollabIdentityUtility.Matches(adminUserId, "", existingId, existingName))
                        continue;

                    var removedName = CollabIdentityUtility.DisplayName(existingId, existingName);
                    doc.adminUsers.RemoveAt(i);
                    if (i < doc.adminUserIds.Count)
                        doc.adminUserIds.RemoveAt(i);
                    changed = true;
                    AppendHistory(doc, requesterId, requesterName, "admin-revoke", "", "", removedName, TimeUtil.NowMs());
                    return true;
                }

                return false;
            }))
            {
                Raise();
            }

            return changed;
        }

        public async Task<bool> DeleteUserAsync(string requesterId, string requesterName, string targetUserId, string targetUserName)
        {
            await Task.Yield();

            bool changed = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                targetUserId = CollabIdentityUtility.Normalize(targetUserId);
                targetUserName = CollabIdentityUtility.Normalize(targetUserName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsRootAdminUser(doc, requesterId, requesterName))
                    return false;

                if (string.IsNullOrEmpty(targetUserId) ||
                    CollabIdentityUtility.Matches(targetUserId, targetUserName, requesterId, requesterName) ||
                    CollabIdentityUtility.Matches(targetUserId, targetUserName, doc.rootAdminUserId, doc.rootAdminUser))
                {
                    return false;
                }

                string displayName = string.IsNullOrEmpty(targetUserName) ? targetUserId : targetUserName;
                if (ContainsAdmin(doc, targetUserId, targetUserName, out var adminIndex))
                {
                    if (adminIndex < doc.adminUsers.Count)
                        displayName = CollabIdentityUtility.DisplayName(
                            adminIndex < doc.adminUserIds.Count ? doc.adminUserIds[adminIndex] : targetUserId,
                            doc.adminUsers[adminIndex]);
                    doc.adminUsers.RemoveAt(adminIndex);
                    if (adminIndex < doc.adminUserIds.Count)
                        doc.adminUserIds.RemoveAt(adminIndex);
                    changed = true;
                }

                int removedPresences = doc.presences.RemoveAll(p => CollabIdentityUtility.Matches(targetUserId, targetUserName, p.userId, p.user));
                int removedLocks = doc.locks.RemoveAll(l => CollabIdentityUtility.Matches(targetUserId, targetUserName, l.ownerId, l.owner));
                if (removedPresences > 0 || removedLocks > 0)
                    changed = true;

                if (ContainsBlocked(doc, targetUserId, targetUserName, out var blockedIndex))
                {
                    if (blockedIndex < doc.blockedUsers.Count && !string.IsNullOrEmpty(displayName))
                        doc.blockedUsers[blockedIndex] = displayName;
                }
                else
                {
                    doc.blockedUserIds.Add(targetUserId);
                    doc.blockedUsers.Add(displayName);
                    changed = true;
                }

                if (!changed)
                    return false;

                AppendHistory(doc, requesterId, requesterName, "user-delete", "", "", displayName, TimeUtil.NowMs());
                return true;
            }))
            {
                Raise();
            }

            return changed;
        }

        public async Task<bool> RestoreUserAsync(string requesterId, string requesterName, string targetUserId, string targetUserName)
        {
            await Task.Yield();

            bool changed = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                targetUserId = CollabIdentityUtility.Normalize(targetUserId);
                targetUserName = CollabIdentityUtility.Normalize(targetUserName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsRootAdminUser(doc, requesterId, requesterName))
                    return false;

                if (string.IsNullOrEmpty(targetUserId))
                    return false;
                if (!ContainsBlocked(doc, targetUserId, targetUserName, out var blockedIndex))
                    return false;

                var displayName = targetUserName;
                if (blockedIndex < doc.blockedUsers.Count)
                {
                    displayName = CollabIdentityUtility.DisplayName(
                        blockedIndex < doc.blockedUserIds.Count ? doc.blockedUserIds[blockedIndex] : targetUserId,
                        doc.blockedUsers[blockedIndex]);
                    doc.blockedUsers.RemoveAt(blockedIndex);
                    if (blockedIndex < doc.blockedUserIds.Count)
                        doc.blockedUserIds.RemoveAt(blockedIndex);
                    changed = true;
                }

                if (!changed)
                    return false;

                AppendHistory(doc, requesterId, requesterName, "user-restore", "", "", displayName, TimeUtil.NowMs());
                return true;
            }))
            {
                Raise();
            }

            return changed;
        }

        public async Task<bool> SetWorkHistoryEnabledAsync(string requesterId, string requesterName, bool enabled)
        {
            await Task.Yield();

            bool changed = false;
            if (MutateDocument(doc =>
            {
                requesterId = CollabIdentityUtility.Normalize(requesterId);
                requesterName = CollabIdentityUtility.Normalize(requesterName);
                if (IsBlockedUser(doc, requesterId, requesterName))
                    return false;
                EnsureAdminBootstrap(doc, requesterId, requesterName);
                if (!IsAdminUser(doc, requesterId, requesterName) || IsWorkHistoryEnabled(doc) == enabled)
                    return false;

                if (enabled)
                {
                    doc.workHistoryMode = "enabled";
                    AppendHistory(doc, requesterId, requesterName, "history-setting", "", "", "enabled", TimeUtil.NowMs());
                }
                else
                {
                    AppendHistory(doc, requesterId, requesterName, "history-setting", "", "", "disabled", TimeUtil.NowMs());
                    doc.workHistoryMode = "disabled";
                }

                changed = true;
                return true;
            }))
            {
                Raise();
            }

            return changed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch
                {
                }

                _watcher = null;
            }

            _onUpdate = null;
        }
    }
}
