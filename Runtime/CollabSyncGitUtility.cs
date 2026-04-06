using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public readonly struct GitLockSnapshot
    {
        public readonly string branchName;
        public readonly string headCommit;
        public readonly string protectedRef;

        public GitLockSnapshot(string branchName, string headCommit, string protectedRef)
        {
            this.branchName = branchName ?? "";
            this.headCommit = headCommit ?? "";
            this.protectedRef = protectedRef ?? "";
        }

        public bool HasHeadCommit => !string.IsNullOrEmpty(headCommit);
        public bool HasProtectedRef => !string.IsNullOrEmpty(protectedRef);

        public bool IsOnProtectedBranch =>
            string.Equals(
                CollabSyncGitUtility.NormalizeBranchLabel(branchName),
                CollabSyncGitUtility.NormalizeBranchLabel(protectedRef),
                StringComparison.Ordinal);
    }

    public static class CollabSyncGitUtility
    {
        public const string RetainedLockState = "retained";
        const int GitCommandTimeoutMs = 1500;

        static readonly string[] ProtectedRefCandidates =
        {
            "origin/main",
            "main",
            "origin/master",
            "master"
        };

        public static bool IsRetainedLock(LockItem lockItem)
        {
            return lockItem != null
                && string.Equals(lockItem.state, RetainedLockState, StringComparison.Ordinal);
        }

        public static bool IsAutoLockReason(string reason)
        {
            return !string.IsNullOrEmpty(reason)
                && reason.StartsWith("auto-lock", StringComparison.Ordinal);
        }

        public static string NormalizeBranchLabel(string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.StartsWith("refs/heads/", StringComparison.Ordinal))
                return normalized.Substring("refs/heads/".Length);
            if (normalized.StartsWith("refs/remotes/", StringComparison.Ordinal))
                return normalized.Substring("refs/remotes/".Length);
            if (normalized.StartsWith("origin/", StringComparison.Ordinal))
                return normalized.Substring("origin/".Length);
            return normalized;
        }

        public static string FormatShortCommit(string commit)
        {
            var normalized = (commit ?? "").Trim();
            return normalized.Length <= 7 ? normalized : normalized.Substring(0, 7);
        }

        public static bool TryCaptureCurrentLockSnapshot(out GitLockSnapshot snapshot)
        {
            snapshot = default;

            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return false;

            var headCommit = ReadHeadCommit(projectRoot);
            if (string.IsNullOrEmpty(headCommit))
                return false;

            snapshot = new GitLockSnapshot(
                ReadCurrentBranch(projectRoot),
                headCommit,
                ResolveProtectedRef(projectRoot));
            return true;
        }

        public static void ApplySnapshot(LockItem lockItem, GitLockSnapshot snapshot, bool retained)
        {
            if (lockItem == null)
                return;

            lockItem.gitBranch = snapshot.branchName ?? "";
            lockItem.gitHeadCommit = snapshot.headCommit ?? "";
            lockItem.gitProtectedBranch = snapshot.protectedRef ?? "";
            lockItem.state = retained ? RetainedLockState : "";
            lockItem.retainedAt = retained ? TimeUtil.NowMs() : 0;
        }

        public static bool ShouldRetainLockOnRelease(LockItem lockItem, GitLockSnapshot snapshot)
        {
            if (lockItem == null
                || IsRetainedLock(lockItem)
                || lockItem.ttlMs > 0
                || IsAutoLockReason(lockItem.reason)
                || string.IsNullOrEmpty(lockItem.gitHeadCommit)
                || !snapshot.HasHeadCommit
                || !snapshot.HasProtectedRef
                || snapshot.IsOnProtectedBranch)
            {
                return false;
            }

            if (string.Equals(snapshot.headCommit, lockItem.gitHeadCommit, StringComparison.Ordinal))
                return false;

            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return false;

            return !IsCommitReachableFromRef(projectRoot, snapshot.headCommit, snapshot.protectedRef);
        }

        public static bool CanReleaseRetainedLock(LockItem lockItem)
        {
            if (!IsRetainedLock(lockItem)
                || string.IsNullOrEmpty(lockItem.gitHeadCommit)
                || string.IsNullOrEmpty(lockItem.gitProtectedBranch))
            {
                return false;
            }

            var projectRoot = FindGitProjectRoot(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot))
                return false;

            return IsCommitReachableFromRef(projectRoot, lockItem.gitHeadCommit, lockItem.gitProtectedBranch);
        }

        public static string BuildRetainedHistoryDetail(GitLockSnapshot snapshot)
        {
            var branchLabel = NormalizeBranchLabel(snapshot.branchName);
            var protectedLabel = NormalizeBranchLabel(snapshot.protectedRef);
            var shortCommit = FormatShortCommit(snapshot.headCommit);
            return CollabSyncLocalization.F(
                "Retained from {0} until {1} contains {2}.",
                "{0} からの変更が {1} に {2} として入るまで保持します。",
                string.IsNullOrEmpty(branchLabel) ? "(detached)" : branchLabel,
                string.IsNullOrEmpty(protectedLabel) ? "main" : protectedLabel,
                string.IsNullOrEmpty(shortCommit) ? "HEAD" : shortCommit);
        }

        static string FindGitProjectRoot(string startDirectory)
        {
            var dir = startDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                var dotGit = Path.Combine(dir, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit))
                    return dir;

                dir = Directory.GetParent(dir)?.FullName;
            }

            return "";
        }

        static string ResolveGitDirectory(string projectRoot)
        {
            var dotGit = Path.Combine(projectRoot, ".git");
            if (Directory.Exists(dotGit))
                return dotGit;

            if (!File.Exists(dotGit))
                return "";

            var content = File.ReadAllText(dotGit).Trim();
            if (!content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                return "";

            var rawPath = content.Substring("gitdir:".Length).Trim();
            if (Path.IsPathRooted(rawPath))
                return rawPath;

            return Path.GetFullPath(Path.Combine(projectRoot, rawPath));
        }

        static string ReadCurrentBranch(string projectRoot)
        {
            try
            {
                var gitDirectory = ResolveGitDirectory(projectRoot);
                if (string.IsNullOrEmpty(gitDirectory))
                    return "";

                var headPath = Path.Combine(gitDirectory, "HEAD");
                if (!File.Exists(headPath))
                    return "";

                var headValue = File.ReadAllText(headPath).Trim();
                if (!headValue.StartsWith("ref:", StringComparison.Ordinal))
                    return "(detached)";

                return NormalizeBranchLabel(headValue.Substring(4).Trim());
            }
            catch
            {
                return "";
            }
        }

        static string ReadHeadCommit(string projectRoot)
        {
            try
            {
                var gitDirectory = ResolveGitDirectory(projectRoot);
                if (string.IsNullOrEmpty(gitDirectory))
                    return "";

                var headPath = Path.Combine(gitDirectory, "HEAD");
                if (!File.Exists(headPath))
                    return "";

                var headValue = File.ReadAllText(headPath).Trim();
                if (!headValue.StartsWith("ref:", StringComparison.Ordinal))
                    return headValue;

                var refName = headValue.Substring(4).Trim();
                var refPath = Path.Combine(gitDirectory, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refPath))
                    return File.ReadAllText(refPath).Trim();

                if (TryRunGit(projectRoot, "rev-parse HEAD", out var output, out var exitCode) && exitCode == 0)
                    return output;

                return "";
            }
            catch
            {
                return "";
            }
        }

        static string ResolveProtectedRef(string projectRoot)
        {
            foreach (var candidate in ProtectedRefCandidates)
            {
                if (RefExists(projectRoot, candidate))
                    return candidate;
            }

            if (TryRunGit(projectRoot, "symbolic-ref --quiet refs/remotes/origin/HEAD", out var output, out var exitCode)
                && exitCode == 0
                && !string.IsNullOrWhiteSpace(output))
            {
                var normalized = NormalizeRemoteHeadOutput(output);
                if (!string.IsNullOrEmpty(normalized) && RefExists(projectRoot, normalized))
                    return normalized;
            }

            return "";
        }

        static string NormalizeRemoteHeadOutput(string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.StartsWith("refs/remotes/", StringComparison.Ordinal))
                normalized = normalized.Substring("refs/remotes/".Length);
            return normalized;
        }

        static bool RefExists(string projectRoot, string refName)
        {
            return TryRunGit(
                       projectRoot,
                       $"rev-parse --verify --quiet {QuoteArg(refName)}",
                       out _,
                       out var exitCode)
                   && exitCode == 0;
        }

        static bool IsCommitReachableFromRef(string projectRoot, string commit, string refName)
        {
            return TryRunGit(
                       projectRoot,
                       $"merge-base --is-ancestor {QuoteArg(commit)} {QuoteArg(refName)}",
                       out _,
                       out var exitCode)
                   && exitCode == 0;
        }

        static bool TryRunGit(string projectRoot, string arguments, out string output, out int exitCode)
        {
            output = "";
            exitCode = -1;

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"-C {QuoteArg(projectRoot)} {arguments}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                if (!process.Start())
                    return false;

                output = process.StandardOutput.ReadToEnd().Trim();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(GitCommandTimeoutMs))
                {
                    try { process.Kill(); }
                    catch { }
                    return false;
                }

                exitCode = process.ExitCode;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string QuoteArg(string value)
        {
            var normalized = value ?? "";
            return "\"" + normalized.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
