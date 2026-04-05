#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

[InitializeOnLoad]
public static class CollabSyncPackageInstaller
{
    const string PackageName = "com.ignoranz.collabsync";
    const string InstallAssetPath = "Assets/IGNORANZ PROJECT/CollabSync";
    const string SessionKey = "CollabSync.PackageInstaller.Running";

    static readonly List<string> s_copiedTopLevelPaths = new();
    static RemoveRequest s_removeRequest;
    static bool s_autoRefreshSuspended;

    static CollabSyncPackageInstaller()
    {
        EditorApplication.delayCall += TryInstallFromGitPackage;
    }

    static void TryInstallFromGitPackage()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryInstallFromGitPackage;
            return;
        }

        if (s_removeRequest != null || SessionState.GetBool(SessionKey, false))
            return;

        var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CollabSyncPackageInstaller).Assembly);
        if (packageInfo == null || !string.Equals(packageInfo.name, PackageName, StringComparison.Ordinal))
            return;
        if (packageInfo.source != UnityEditor.PackageManager.PackageSource.Git)
            return;
        if (string.IsNullOrEmpty(packageInfo.resolvedPath) || !Directory.Exists(packageInfo.resolvedPath))
            return;

        try
        {
            SessionState.SetBool(SessionKey, true);
            AssetDatabase.DisallowAutoRefresh();
            s_autoRefreshSuspended = true;

            InstallPackageContents(packageInfo.resolvedPath);

            Debug.Log("[CollabSync] Installed package contents to " + InstallAssetPath + ". Removing the temporary Package Manager entry.");
            s_removeRequest = Client.Remove(PackageName);
            EditorApplication.update += WaitForPackageRemoval;
        }
        catch (Exception ex)
        {
            CleanupCopiedFiles();
            FinishInstall();
            Debug.LogError("[CollabSync] Package install failed: " + ex.Message);
        }
    }

    static void InstallPackageContents(string packageRoot)
    {
        var projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
            throw new InvalidOperationException("Project root could not be resolved.");

        var installRoot = Path.Combine(projectRoot, "Assets", "IGNORANZ PROJECT", "CollabSync");
        CopyTopLevelFolderIfNeeded(Path.Combine(packageRoot, "Editor"), Path.Combine(installRoot, "Editor"));
        CopyTopLevelFolderIfNeeded(Path.Combine(packageRoot, "Runtime"), Path.Combine(installRoot, "Runtime"));
    }

    static void CopyTopLevelFolderIfNeeded(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
            return;
        if (Directory.Exists(destinationPath))
            return;

        CopyDirectoryRecursive(sourcePath, destinationPath);
        s_copiedTopLevelPaths.Add(destinationPath);
    }

    static void CopyDirectoryRecursive(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var name = Path.GetFileName(directory);
            if (ShouldSkipPath(name))
                continue;

            CopyDirectoryRecursive(directory, Path.Combine(destinationPath, name));
        }

        foreach (var file in Directory.GetFiles(sourcePath))
        {
            var name = Path.GetFileName(file);
            if (ShouldSkipPath(name))
                continue;

            File.Copy(file, Path.Combine(destinationPath, name), overwrite: true);
        }
    }

    static bool ShouldSkipPath(string name)
    {
        return string.IsNullOrEmpty(name)
            || name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal)
            || string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase);
    }

    static void WaitForPackageRemoval()
    {
        if (s_removeRequest == null || !s_removeRequest.IsCompleted)
            return;

        EditorApplication.update -= WaitForPackageRemoval;

        if (s_removeRequest.Status == StatusCode.Success)
        {
            Debug.Log("[CollabSync] Package installation finished.");
        }
        else
        {
            CleanupCopiedFiles();
            Debug.LogError("[CollabSync] Failed to remove the temporary Package Manager entry. Remove com.ignoranz.collabsync manually and retry.");
        }

        FinishInstall();
    }

    static void CleanupCopiedFiles()
    {
        foreach (var path in s_copiedTopLevelPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    FileUtil.DeleteFileOrDirectory(path);
            }
            catch (Exception ex)
            {
                Debug.LogError("[CollabSync] Cleanup error: " + ex.Message);
            }
        }

        s_copiedTopLevelPaths.Clear();
    }

    static void FinishInstall()
    {
        s_removeRequest = null;
        s_copiedTopLevelPaths.Clear();
        SessionState.SetBool(SessionKey, false);

        if (s_autoRefreshSuspended)
        {
            AssetDatabase.AllowAutoRefresh();
            s_autoRefreshSuspended = false;
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }
}
#endif
