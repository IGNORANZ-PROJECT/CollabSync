using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncBackendUtility
    {
        public const string DefaultLocalJsonPath = "ProjectSettings/CollabSyncLocal.json";

        static readonly Dictionary<string, string> s_lastErrorByCaller = new();
        static readonly object s_backendLock = new();
        static string s_cachedBackendPath = "";
        static ICollabBackend s_cachedBackend;

        public static bool TryCreateBackend(
            CollabSyncConfig cfg,
            out ICollabBackend backend,
            out string resolvedPath,
            out string statusOrError)
        {
            backend = null;
            resolvedPath = "";

            if (!TryResolveJsonPath(cfg, out resolvedPath, out statusOrError))
                return false;

            lock (s_backendLock)
            {
                if (s_cachedBackend != null && string.Equals(s_cachedBackendPath, resolvedPath, StringComparison.Ordinal))
                {
                    backend = s_cachedBackend;
                    statusOrError = resolvedPath;
                    return true;
                }

                if (s_cachedBackend is IDisposable disposable)
                    disposable.Dispose();

                s_cachedBackendPath = resolvedPath;
                s_cachedBackend = new LocalJsonBackend(resolvedPath);
                backend = s_cachedBackend;
            }

            statusOrError = resolvedPath;
            return true;
        }

        public static bool TryResolveJsonPath(
            CollabSyncConfig cfg,
            out string path,
            out string statusOrError)
        {
            path = "";

            if (cfg == null)
            {
                statusOrError = CollabSyncLocalization.T(
                    "CollabSyncConfig could not be loaded.",
                    "CollabSyncConfig を読み込めませんでした。");
                return false;
            }

            path = ExpandPath(cfg.localJsonPath);
            if (string.IsNullOrWhiteSpace(path))
                path = DefaultLocalJsonPath;

            if (LooksLikeAnyUrl(path))
            {
                statusOrError = CollabSyncLocalization.T(
                    "JSON Path must be a local or network file path, not a URL.",
                    "JSON パスには URL ではなく、ローカルまたはネットワーク上のファイルパスを指定してください。");
                return false;
            }

            statusOrError = CollabSyncLocalization.F("Shared JSON: {0}", "共有 JSON: {0}", path);
            return true;
        }

        public static string FormatStorageLabel(CollabSyncConfig cfg)
        {
            if (cfg == null) return CollabSyncLocalization.T("Shared JSON", "共有 JSON");

            return TryResolveJsonPath(cfg, out var path, out _)
                ? CollabSyncLocalization.F("Shared JSON: {0}", "共有 JSON: {0}", path)
                : CollabSyncLocalization.T("Shared JSON: unresolved", "共有 JSON: 未解決");
        }

        public static void LogUnavailableOnce(string caller, string statusOrError)
        {
            if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(statusOrError))
                return;

            if (s_lastErrorByCaller.TryGetValue(caller, out var last) && last == statusOrError)
                return;

            s_lastErrorByCaller[caller] = statusOrError;
            Debug.LogWarning($"[CollabSync] {caller}: {statusOrError}");
        }

        public static void ClearLoggedError(string caller)
        {
            if (string.IsNullOrWhiteSpace(caller)) return;
            s_lastErrorByCaller.Remove(caller);
        }

        public static string NormalizeStoredPathInput(string value)
        {
            var normalized = (value ?? "").Trim();
            if (string.IsNullOrEmpty(normalized))
                return normalized;

            normalized = TrimWrappingQuotes(normalized);

            var embeddedAbsolute = TryExtractQuotedAbsolutePath(normalized);
            if (!string.IsNullOrEmpty(embeddedAbsolute))
                normalized = TrimWrappingQuotes(embeddedAbsolute);

            return normalized;
        }

        static string ExpandPath(string value)
        {
            var expanded = Environment.ExpandEnvironmentVariables(NormalizeStoredPathInput(value));
            if (string.IsNullOrWhiteSpace(expanded)) return expanded;

            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var tail = expanded.Substring(1).TrimStart('/', '\\');
                expanded = string.IsNullOrEmpty(tail) ? home : Path.Combine(home, tail);
            }

            try
            {
                if (!LooksLikeAnyUrl(expanded))
                    expanded = Path.GetFullPath(expanded);
            }
            catch
            {
            }

            return expanded;
        }

        static string TrimWrappingQuotes(string value)
        {
            var result = (value ?? "").Trim();
            while (result.Length >= 2)
            {
                bool wrappedBySingle = result[0] == '\'' && result[^1] == '\'';
                bool wrappedByDouble = result[0] == '"' && result[^1] == '"';
                if (!wrappedBySingle && !wrappedByDouble)
                    break;

                result = result.Substring(1, result.Length - 2).Trim();
            }

            if (result.StartsWith("'", StringComparison.Ordinal) || result.StartsWith("\"", StringComparison.Ordinal))
                result = result.Substring(1).TrimStart();
            if (result.EndsWith("'", StringComparison.Ordinal) || result.EndsWith("\"", StringComparison.Ordinal))
                result = result.Substring(0, result.Length - 1).TrimEnd();

            return result;
        }

        static string TryExtractQuotedAbsolutePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            string best = "";
            for (int i = 0; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c != '\'' && c != '"')
                    continue;

                var candidate = value.Substring(i + 1).Trim();
                if (LooksLikeAbsoluteFilePath(candidate))
                    best = candidate;
            }

            return best;
        }

        static bool LooksLikeAbsoluteFilePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("/", StringComparison.Ordinal))
                return true;
            if (value.StartsWith("\\\\", StringComparison.Ordinal))
                return true;
            if (value.StartsWith("~/", StringComparison.Ordinal) || value == "~")
                return true;
            if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
                return true;

            return false;
        }

        static bool LooksLikeAnyUrl(string value)
        {
            return Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
