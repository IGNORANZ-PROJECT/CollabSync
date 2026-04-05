using UnityEngine;

namespace Ignoranz.CollabSync
{
    public enum CollabSyncLanguageMode
    {
        Auto = 0,
        English = 1,
        Japanese = 2
    }

    [CreateAssetMenu(fileName = "CollabSyncConfig", menuName = "IGNORANZ-PROJECT/CollabSyncConfig")]
    public class CollabSyncConfig : ScriptableObject
    {
        public string projectId = "IGNORANZ_PROJECT_DEFAULT";

        [Header("Shared State File")]
        public string localJsonPath = CollabSyncBackendUtility.DefaultLocalJsonPath;

        [Header("Notifications")]
        public bool notifyOnNewMemo = true;
        public bool beepOnNewMemo = false;

        [Header("Language")]
        public CollabSyncLanguageMode languageMode = CollabSyncLanguageMode.Auto;

        [HideInInspector] public string userDisplayName = "";

        public bool TryGetResolvedJsonPath(out string path, out string statusOrError)
        {
            return CollabSyncBackendUtility.TryResolveJsonPath(this, out path, out statusOrError);
        }

        public string GetStorageStatus()
        {
            return CollabSyncBackendUtility.TryResolveJsonPath(this, out var path, out var statusOrError)
                ? path
                : statusOrError;
        }

#if UNITY_EDITOR
        public static CollabSyncConfig LoadOrCreate()
        {
            const string ProjectAssetPath = "Assets/IGNORANZ-PROJECT/Runtime/Resources/CollabSyncConfig.asset";
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<CollabSyncConfig>(ProjectAssetPath);
            if (!asset)
            {
                asset = ScriptableObject.CreateInstance<CollabSyncConfig>();
                System.IO.Directory.CreateDirectory("Assets/IGNORANZ-PROJECT/Runtime/Resources");
                UnityEditor.AssetDatabase.CreateAsset(asset, ProjectAssetPath);
                UnityEditor.AssetDatabase.SaveAssets();
            }
            return asset;
        }
#else
        public static CollabSyncConfig LoadOrCreate()
        {
            var asset = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
            if (!asset) asset = ScriptableObject.CreateInstance<CollabSyncConfig>();
            return asset;
        }
#endif
    }
}
