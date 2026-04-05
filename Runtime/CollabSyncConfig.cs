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
        const string ProjectLocalEditorAssetPath = "Assets/IGNORANZ-PROJECT/CollabSyncSettings/Resources/CollabSyncConfig.asset";

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
            var asset = FindProjectLocalEditorAsset();
            if (!asset)
                asset = TryCreateProjectLocalCopyFromPackagedAsset();

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

        static CollabSyncConfig FindProjectLocalEditorAsset()
        {
            var existingGuids = AssetDatabase.FindAssets("t:CollabSyncConfig CollabSyncConfig");
            foreach (var guid in existingGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !IsProjectLocalEditorAssetPath(path))
                    continue;

                var loaded = AssetDatabase.LoadAssetAtPath<CollabSyncConfig>(path);
                if (loaded != null)
                    return loaded;
            }

            return null;
        }

        static CollabSyncConfig FindPackagedEditorAsset()
        {
            var packagedResource = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
            if (packagedResource != null)
            {
                var resourcePath = AssetDatabase.GetAssetPath(packagedResource);
                if (IsPackagedAssetPath(resourcePath))
                    return packagedResource;
            }

            var existingGuids = AssetDatabase.FindAssets("t:CollabSyncConfig CollabSyncConfig");
            foreach (var guid in existingGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !IsPackagedAssetPath(path))
                    continue;

                var loaded = AssetDatabase.LoadAssetAtPath<CollabSyncConfig>(path);
                if (loaded != null)
                    return loaded;
            }

            return null;
        }

        static CollabSyncConfig TryCreateProjectLocalCopyFromPackagedAsset()
        {
            var source = FindPackagedEditorAsset();
            if (source == null)
                return null;

            var asset = ScriptableObject.CreateInstance<CollabSyncConfig>();
            EditorUtility.CopySerialized(source, asset);

            var assetPath = ResolveEditorAssetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? "Assets");
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        static string ResolveEditorAssetPath()
        {
            var existingAsset = FindProjectLocalEditorAsset();
            if (existingAsset != null)
                return AssetDatabase.GetAssetPath(existingAsset);

            return ProjectLocalEditorAssetPath;
        }

        static bool IsProjectLocalEditorAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.StartsWith("Assets/", System.StringComparison.Ordinal);
        }

        static bool IsPackagedAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.StartsWith("Packages/", System.StringComparison.Ordinal);
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
