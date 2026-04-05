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
        const string EditorPrefsLocalJsonPathKeyPrefix = "Ignoranz.CollabSync.LocalJsonPath.";
#if UNITY_EDITOR
        static CollabSyncConfig s_cachedEditorAsset;
#endif

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
            if (s_cachedEditorAsset != null)
            {
                RestoreEditorLocalJsonPathPreference(s_cachedEditorAsset);
                return s_cachedEditorAsset;
            }

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

            RestoreEditorLocalJsonPathPreference(asset);
            s_cachedEditorAsset = asset;
            return asset;
        }

        public static bool SetEditorLocalJsonPath(CollabSyncConfig asset, string value)
        {
            if (asset == null)
                return false;

            var normalized = CollabSyncBackendUtility.NormalizeStoredPathInput(value);
            var changed = !string.Equals(asset.localJsonPath ?? "", normalized ?? "", System.StringComparison.Ordinal);
            if (changed)
                asset.localJsonPath = normalized;

            StoreEditorLocalJsonPathPreference(normalized);

            if (!changed)
                return false;

            SaveEditorAsset(asset, persistLocalJsonPath: false);
            return true;
        }

        public static void SaveEditorAsset(CollabSyncConfig asset)
        {
            SaveEditorAsset(asset, persistLocalJsonPath: true);
        }

        static CollabSyncConfig FindProjectLocalEditorAsset()
        {
            if (s_cachedEditorAsset != null)
            {
                var cachedPath = AssetDatabase.GetAssetPath(s_cachedEditorAsset);
                if (IsProjectLocalEditorAssetPath(cachedPath))
                    return s_cachedEditorAsset;

                s_cachedEditorAsset = null;
            }

            var existingGuids = AssetDatabase.FindAssets("t:CollabSyncConfig CollabSyncConfig");
            foreach (var guid in existingGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !IsProjectLocalEditorAssetPath(path))
                    continue;

                var loaded = AssetDatabase.LoadAssetAtPath<CollabSyncConfig>(path);
                if (loaded != null)
                {
                    s_cachedEditorAsset = loaded;
                    return loaded;
                }
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
            s_cachedEditorAsset = asset;
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

        static void SaveEditorAsset(CollabSyncConfig asset, bool persistLocalJsonPath)
        {
            if (asset == null)
                return;

            if (persistLocalJsonPath)
                StoreEditorLocalJsonPathPreference(asset.localJsonPath);

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        static void RestoreEditorLocalJsonPathPreference(CollabSyncConfig asset)
        {
            if (asset == null)
                return;

            var assetValue = CollabSyncBackendUtility.NormalizeStoredPathInput(asset.localJsonPath);
            if (!string.Equals(asset.localJsonPath ?? "", assetValue ?? "", System.StringComparison.Ordinal))
            {
                asset.localJsonPath = assetValue;
                SaveEditorAsset(asset);
                return;
            }

            var storedValue = CollabSyncBackendUtility.NormalizeStoredPathInput(
                EditorPrefs.GetString(GetEditorLocalJsonPathPreferenceKey(), ""));

            if (!string.IsNullOrEmpty(storedValue)
                && (string.IsNullOrEmpty(assetValue)
                    || string.Equals(assetValue, CollabSyncBackendUtility.DefaultLocalJsonPath, System.StringComparison.Ordinal))
                && !string.Equals(assetValue, storedValue, System.StringComparison.Ordinal))
            {
                asset.localJsonPath = storedValue;
                SaveEditorAsset(asset);
                return;
            }

            StoreEditorLocalJsonPathPreference(asset.localJsonPath);
        }

        static void StoreEditorLocalJsonPathPreference(string value)
        {
            var normalized = CollabSyncBackendUtility.NormalizeStoredPathInput(value);
            var key = GetEditorLocalJsonPathPreferenceKey();
            if (string.IsNullOrEmpty(normalized))
            {
                EditorPrefs.DeleteKey(key);
                return;
            }

            EditorPrefs.SetString(key, normalized);
        }

        static string GetEditorLocalJsonPathPreferenceKey()
        {
            var projectPath = Path.GetFullPath(Directory.GetCurrentDirectory());
            return EditorPrefsLocalJsonPathKeyPrefix + Hash128.Compute(projectPath).ToString();
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
