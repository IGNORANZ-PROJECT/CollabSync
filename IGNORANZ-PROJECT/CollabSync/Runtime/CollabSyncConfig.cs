using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
#endif

namespace Ignoranz.CollabSync
{
    public enum CollabSyncLanguageMode
    {
        Auto = 0,
        English = 1,
        Japanese = 2
    }

    [CreateAssetMenu(fileName = "CollabSyncConfig", menuName = "IGNORANZ PROJECT/CollabSyncConfig")]
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
            var asset = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
            if (!asset)
                asset = FindExistingEditorAsset();

            if (!asset)
            {
                asset = ScriptableObject.CreateInstance<CollabSyncConfig>();
                var assetPath = ResolveEditorAssetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? "Assets");
                AssetDatabase.CreateAsset(asset, assetPath);
                AssetDatabase.SaveAssets();
            }
            return asset;
        }

        static CollabSyncConfig FindExistingEditorAsset()
        {
            var existingGuids = AssetDatabase.FindAssets("t:CollabSyncConfig CollabSyncConfig");
            foreach (var guid in existingGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    continue;

                var loaded = AssetDatabase.LoadAssetAtPath<CollabSyncConfig>(path);
                if (loaded != null)
                    return loaded;
            }

            return null;
        }

        static string ResolveEditorAssetPath()
        {
            var existingAsset = FindExistingEditorAsset();
            if (existingAsset != null)
                return AssetDatabase.GetAssetPath(existingAsset);

            var scriptGuids = AssetDatabase.FindAssets("CollabSyncConfig t:Script");
            foreach (var guid in scriptGuids)
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(scriptPath))
                    continue;

                var runtimeDir = Path.GetDirectoryName(scriptPath);
                if (string.IsNullOrEmpty(runtimeDir))
                    continue;

                return Path.Combine(runtimeDir, "Resources", "CollabSyncConfig.asset").Replace('\\', '/');
            }

            return "Assets/IGNORANZ-PROJECT/CollabSync/Runtime/Resources/CollabSyncConfig.asset";
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
