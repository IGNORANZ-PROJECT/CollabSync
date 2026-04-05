#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncUser
    {
        static readonly string NameKey = $"CollabSync.UserName.{Application.dataPath.GetHashCode()}";
        static readonly string IdKey = $"CollabSync.UserId.{Application.dataPath.GetHashCode()}";

        public static string UserId
        {
            get
            {
                var userId = EditorPrefs.GetString(IdKey, "");
                if (string.IsNullOrWhiteSpace(userId))
                {
                    userId = GUID.Generate().ToString();
                    EditorPrefs.SetString(IdKey, userId);
                }

                return userId;
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
    }
}
#endif
