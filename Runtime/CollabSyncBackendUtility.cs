using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncBackendUtility
    {
        public const string DefaultLocalJsonPath = "ProjectSettings/CollabSyncLocal.json";

        static readonly Dictionary<string, string> s_lastErrorByCaller = new();
        static readonly object s_backendLock = new();
        static string s_cachedBackendKey = "";
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
                var backendKey = resolvedPath + "\n" + CollabSyncProtectedStateUtility.NormalizeProjectId(cfg.projectId);
                if (s_cachedBackend != null && string.Equals(s_cachedBackendKey, backendKey, StringComparison.Ordinal))
                {
                    backend = s_cachedBackend;
                    statusOrError = resolvedPath;
                    return true;
                }

                if (s_cachedBackend is IDisposable disposable)
                    disposable.Dispose();

                s_cachedBackendKey = backendKey;
                s_cachedBackend = new LocalJsonBackend(resolvedPath, cfg.projectId);
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

    [Serializable]
    public sealed class CollabSyncProtectedPayloadEnvelope
    {
        public string format = "";
        public string purpose = "";
        public string iv = "";
        public string cipherText = "";
        public string mac = "";
    }

    public static class CollabSyncProtectedStateUtility
    {
        const string PayloadFormat = "collabsync-protected-v1";
        const string SharedStatePurpose = "shared-state";
        const string LocalIdentityPurpose = "local-identity";

        public static string NormalizeProjectId(string projectId)
        {
            var normalized = (projectId ?? "").Trim();
            return string.IsNullOrEmpty(normalized)
                ? CollabSyncConfig.DefaultProjectId
                : normalized;
        }

        public static string ProtectSharedStateJson(string plainJson, string projectId)
        {
            return ProtectString(plainJson, SharedStatePurpose, BuildSharedStateSeed(projectId));
        }

        public static bool TryReadSharedStateJson(string storageText, string projectId, out string plainJson, out bool wasProtected)
        {
            if (TryUnprotectString(storageText, SharedStatePurpose, EnumerateSharedStateSeeds(projectId), out plainJson))
            {
                wasProtected = true;
                return true;
            }

            if (IsProtectedPayload(storageText))
            {
                plainJson = null;
                wasProtected = true;
                return false;
            }

            plainJson = storageText ?? "";
            wasProtected = false;
            return true;
        }

        public static string ProtectLocalIdentityJson(string plainJson, string projectId)
        {
            return ProtectString(plainJson, LocalIdentityPurpose, BuildLocalIdentitySeed(projectId));
        }

        public static bool TryReadLocalIdentityJson(string storageText, string projectId, out string plainJson)
        {
            return TryUnprotectString(storageText, LocalIdentityPurpose, EnumerateLocalIdentitySeeds(projectId), out plainJson);
        }

        public static bool IsProtectedPayload(string storageText)
        {
            return TryParseEnvelope(storageText, out _);
        }

        static IEnumerable<string> EnumerateSharedStateSeeds(string projectId)
        {
            var normalized = NormalizeProjectId(projectId);
            yield return BuildSharedStateSeed(normalized);
            if (!string.Equals(normalized, CollabSyncConfig.DefaultProjectId, StringComparison.Ordinal))
                yield return BuildSharedStateSeed(CollabSyncConfig.DefaultProjectId);
        }

        static IEnumerable<string> EnumerateLocalIdentitySeeds(string projectId)
        {
            var normalized = NormalizeProjectId(projectId);
            yield return BuildLocalIdentitySeed(normalized);
            if (!string.Equals(normalized, CollabSyncConfig.DefaultProjectId, StringComparison.Ordinal))
                yield return BuildLocalIdentitySeed(CollabSyncConfig.DefaultProjectId);
        }

        static string BuildSharedStateSeed(string projectId)
        {
            return BuildSeed(SharedStatePurpose, NormalizeProjectId(projectId), includeDeviceBinding: false);
        }

        static string BuildLocalIdentitySeed(string projectId)
        {
            return BuildSeed(LocalIdentityPurpose, NormalizeProjectId(projectId), includeDeviceBinding: true);
        }

        static string BuildSeed(string purpose, string projectId, bool includeDeviceBinding)
        {
            var builder = new StringBuilder();
            builder.Append("CollabSync|")
                .Append(purpose).Append('|')
                .Append(projectId).Append('|')
                .Append(Application.companyName ?? "").Append('|')
                .Append(Application.productName ?? "");

            if (includeDeviceBinding)
                builder.Append('|').Append(SystemInfo.deviceUniqueIdentifier ?? "");

            return builder.ToString();
        }

        static string ProtectString(string plainText, string purpose, string secretSeed)
        {
            plainText ??= "";
            DeriveKeys(secretSeed, out var encryptionKey, out var macKey);

            using var aes = Aes.Create();
            aes.Key = encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes;
            using (var ms = new MemoryStream())
            {
                using (var cryptoStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cryptoStream.Write(plainBytes, 0, plainBytes.Length);
                    cryptoStream.FlushFinalBlock();
                }

                cipherBytes = ms.ToArray();
            }

            var envelope = new CollabSyncProtectedPayloadEnvelope
            {
                format = PayloadFormat,
                purpose = purpose ?? "",
                iv = Convert.ToBase64String(aes.IV),
                cipherText = Convert.ToBase64String(cipherBytes)
            };
            envelope.mac = Convert.ToBase64String(ComputeMac(macKey, envelope));

#if UNITY_2021_2_OR_NEWER
            return JsonUtility.ToJson(envelope, true);
#else
            return JsonUtility.ToJson(envelope);
#endif
        }

        static bool TryUnprotectString(string storageText, string expectedPurpose, IEnumerable<string> secretSeeds, out string plainText)
        {
            plainText = null;
            if (!TryParseEnvelope(storageText, out var envelope))
                return false;
            if (!string.Equals(envelope.purpose ?? "", expectedPurpose ?? "", StringComparison.Ordinal))
                return false;

            if (!TryDecodeBase64(envelope.iv, out var ivBytes) || ivBytes.Length == 0)
                return false;
            if (!TryDecodeBase64(envelope.cipherText, out var cipherBytes))
                return false;
            if (!TryDecodeBase64(envelope.mac, out var expectedMac))
                return false;

            foreach (var secretSeed in secretSeeds ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(secretSeed))
                    continue;

                DeriveKeys(secretSeed, out var encryptionKey, out var macKey);
                var actualMac = ComputeMac(macKey, envelope);
                if (!FixedTimeEquals(expectedMac, actualMac))
                    continue;

                try
                {
                    using var aes = Aes.Create();
                    aes.Key = encryptionKey;
                    aes.IV = ivBytes;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using var output = new MemoryStream();
                    using (var cryptoStream = new CryptoStream(output, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(cipherBytes, 0, cipherBytes.Length);
                        cryptoStream.FlushFinalBlock();
                    }

                    plainText = Encoding.UTF8.GetString(output.ToArray());
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        static bool TryParseEnvelope(string storageText, out CollabSyncProtectedPayloadEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(storageText))
                return false;

            try
            {
                envelope = JsonUtility.FromJson<CollabSyncProtectedPayloadEnvelope>(storageText);
                return envelope != null &&
                       string.Equals(envelope.format ?? "", PayloadFormat, StringComparison.Ordinal) &&
                       !string.IsNullOrEmpty(envelope.purpose) &&
                       !string.IsNullOrEmpty(envelope.iv) &&
                       !string.IsNullOrEmpty(envelope.cipherText) &&
                       !string.IsNullOrEmpty(envelope.mac);
            }
            catch
            {
                envelope = null;
                return false;
            }
        }

        static void DeriveKeys(string secretSeed, out byte[] encryptionKey, out byte[] macKey)
        {
            using var sha = SHA256.Create();
            encryptionKey = sha.ComputeHash(Encoding.UTF8.GetBytes("enc|" + (secretSeed ?? "")));
            macKey = sha.ComputeHash(Encoding.UTF8.GetBytes("mac|" + (secretSeed ?? "")));
        }

        static byte[] ComputeMac(byte[] macKey, CollabSyncProtectedPayloadEnvelope envelope)
        {
            var macInput = Encoding.UTF8.GetBytes(
                (envelope.format ?? "") + "\n" +
                (envelope.purpose ?? "") + "\n" +
                (envelope.iv ?? "") + "\n" +
                (envelope.cipherText ?? ""));

            using var hmac = new HMACSHA256(macKey);
            return hmac.ComputeHash(macInput);
        }

        static bool TryDecodeBase64(string value, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(value ?? "");
                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

#if NET_STANDARD_2_1 || NET_6_0_OR_GREATER
            return CryptographicOperations.FixedTimeEquals(left, right);
#else
            int diff = 0;
            for (int i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];
            return diff == 0;
#endif
        }
    }
}
