#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncUser
    {
        [Serializable]
        sealed class UserIdentityPayload
        {
            public string userId = "";
            public long createdAt;
        }

        static readonly string NameKey = $"CollabSync.UserName.{Application.dataPath.GetHashCode()}";
        static readonly string IdKey = $"CollabSync.UserId.{Application.dataPath.GetHashCode()}";
        static string s_cachedUserId;
        static string s_cachedProjectId;

        static string PrimaryIdentityPath => Path.Combine(Directory.GetCurrentDirectory(), "UserSettings/.collabsync/user.identity");
        static string SecondaryIdentityPath => Path.Combine(Directory.GetCurrentDirectory(), "Library/CollabSync/user.identity");

        public static string UserId
        {
            get
            {
                if (!string.IsNullOrEmpty(s_cachedUserId))
                    return s_cachedUserId;

                s_cachedUserId = LoadOrCreateImmutableUserId();
                PersistLegacyMirror(s_cachedUserId);
                return s_cachedUserId;
            }
        }

        public static string UserName
        {
            get
            {
                var name = EditorPrefs.GetString(NameKey, "");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = SystemInfo.deviceName; // 初期値
                    EditorPrefs.SetString(NameKey, name);
                }
                return name;
            }
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                    EditorPrefs.SetString(NameKey, value.Trim());
            }
        }

        static string LoadOrCreateImmutableUserId()
        {
            var projectId = GetProjectId();
            if (TryLoadIdentity(PrimaryIdentityPath, projectId, out var userId))
            {
                EnsureSecondaryIdentityMirror(userId, projectId);
                return userId;
            }

            if (TryLoadIdentity(SecondaryIdentityPath, projectId, out userId))
            {
                SaveIdentity(PrimaryIdentityPath, userId, projectId);
                return userId;
            }

            var legacyUserId = NormalizeUserId(EditorPrefs.GetString(IdKey, ""));
            if (string.IsNullOrEmpty(legacyUserId))
                legacyUserId = GUID.Generate().ToString();

            SaveIdentity(PrimaryIdentityPath, legacyUserId, projectId);
            SaveIdentity(SecondaryIdentityPath, legacyUserId, projectId);
            return legacyUserId;
        }

        static void EnsureSecondaryIdentityMirror(string userId, string projectId)
        {
            if (TryLoadIdentity(SecondaryIdentityPath, projectId, out var mirroredUserId) &&
                string.Equals(mirroredUserId, userId, StringComparison.Ordinal))
            {
                return;
            }

            SaveIdentity(SecondaryIdentityPath, userId, projectId);
        }

        static bool TryLoadIdentity(string path, string projectId, out string userId)
        {
            userId = "";

            try
            {
                if (!File.Exists(path))
                    return false;

                var storageText = File.ReadAllText(path, Encoding.UTF8);
                if (!CollabSyncProtectedStateUtility.TryReadLocalIdentityJson(storageText, projectId, out var json))
                    return false;

                var payload = JsonUtility.FromJson<UserIdentityPayload>(json);
                userId = NormalizeUserId(payload?.userId);
                return !string.IsNullOrEmpty(userId);
            }
            catch
            {
                return false;
            }
        }

        static void SaveIdentity(string path, string userId, string projectId)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var payload = new UserIdentityPayload
                {
                    userId = NormalizeUserId(userId),
                    createdAt = TimeUtil.NowMs()
                };

#if UNITY_2021_2_OR_NEWER
                var json = JsonUtility.ToJson(payload, true);
#else
                var json = JsonUtility.ToJson(payload);
#endif
                var protectedJson = CollabSyncProtectedStateUtility.ProtectLocalIdentityJson(json, projectId);
                File.WriteAllText(path, protectedJson, Encoding.UTF8);
            }
            catch
            {
            }
        }

        static void PersistLegacyMirror(string userId)
        {
            if (!string.IsNullOrEmpty(userId))
                EditorPrefs.SetString(IdKey, userId);
        }

        static string NormalizeUserId(string userId)
        {
            return (userId ?? "").Trim();
        }

        static string GetProjectId()
        {
            if (!string.IsNullOrEmpty(s_cachedProjectId))
                return s_cachedProjectId;

            var cfg = CollabSyncConfig.LoadOrCreate();
            s_cachedProjectId = CollabSyncProtectedStateUtility.NormalizeProjectId(cfg != null ? cfg.projectId : "");
            return s_cachedProjectId;
        }
    }
}
#endif
