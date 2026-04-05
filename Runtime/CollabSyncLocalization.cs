using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncLocalization
    {
        enum AutoLanguageSource
        {
            None,
            MacUserDefaults,
            EnvironmentVariable,
            CurrentUICulture,
            InstalledUICulture,
            CurrentCulture
        }

        readonly struct AutoLanguageResolution
        {
            public readonly CollabSyncLanguageMode mode;
            public readonly AutoLanguageSource source;
            public readonly string detail;

            public AutoLanguageResolution(CollabSyncLanguageMode mode, AutoLanguageSource source, string detail)
            {
                this.mode = mode;
                this.source = source;
                this.detail = detail ?? "";
            }
        }

        static readonly object s_autoLanguageLock = new();
        static readonly TimeSpan s_autoLanguageCacheDuration = TimeSpan.FromSeconds(10);
        static AutoLanguageResolution s_cachedAutoLanguage;
        static DateTime s_autoLanguageCachedAtUtc;
        static bool s_hasAutoLanguageCache;

        public static bool UseJapanese => GetResolvedLanguageMode() == CollabSyncLanguageMode.Japanese;
        public static CollabSyncLanguageMode CurrentLanguageMode => GetResolvedLanguageMode();

#if UNITY_EDITOR
        public static bool IsJapaneseEditorWarningActive =>
            GetResolvedLanguageMode() == CollabSyncLanguageMode.Japanese
            && IsAffectedUnity6000EditorVersion(Application.unityVersion);

        public static string JapaneseEditorWorkaroundMessage =>
            T(
                "Unity 6000.0.68f1 and earlier 6000.0 builds can throw IMGUI assertion UUM-85059 when Japanese editor text is drawn. Japanese is still selectable, but upgrading to 6000.0.69f1 or newer is recommended.",
                "Unity 6000.0.68f1 以前の 6000.0 系では、日本語 UI 描画時に IMGUI assertion UUM-85059 が出ることがあります。日本語は選択できますが、6000.0.69f1 以降への更新を推奨します。");
#endif

        public static string GetLanguageStatusSummary()
        {
            var configuredMode = LoadConfiguredLanguageMode();
            var resolvedMode = GetResolvedLanguageMode();

            if (configuredMode == CollabSyncLanguageMode.English)
                return T("Current language: English (manual).", "現在の言語: 英語（手動設定）");
            if (configuredMode == CollabSyncLanguageMode.Japanese)
                return T("Current language: Japanese (manual).", "現在の言語: 日本語（手動設定）");

            ResolveAutoLanguage(out _, out var source, out var sourceDetail);
            var resolvedLabel = DescribeMode(resolvedMode);

            if (source == AutoLanguageSource.MacUserDefaults)
            {
                return F(
                    "Current language: {0} (Auto -> macOS preferred language: {1}).",
                    "現在の言語: {0}（自動設定 -> macOS の優先言語: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.EnvironmentVariable)
            {
                return F(
                    "Current language: {0} (Auto -> environment locale: {1}).",
                    "現在の言語: {0}（自動設定 -> 環境ロケール: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.CurrentUICulture)
            {
                return F(
                    "Current language: {0} (Auto -> PC UI culture: {1}).",
                    "現在の言語: {0}（自動設定 -> PC の UI カルチャ: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.InstalledUICulture)
            {
                return F(
                    "Current language: {0} (Auto -> PC installed UI culture: {1}).",
                    "現在の言語: {0}（自動設定 -> PC の既定 UI カルチャ: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.CurrentCulture)
            {
                return F(
                    "Current language: {0} (Auto -> PC culture: {1}).",
                    "現在の言語: {0}（自動設定 -> PC カルチャ: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            return F(
                "Current language: {0} (Auto -> fallback: English).",
                "現在の言語: {0}（自動設定 -> フォールバック: 英語）。",
                resolvedLabel);
        }

        static CollabSyncLanguageMode GetResolvedLanguageMode()
        {
            var configuredMode = LoadConfiguredLanguageMode();
            if (configuredMode == CollabSyncLanguageMode.English)
                return CollabSyncLanguageMode.English;
            if (configuredMode == CollabSyncLanguageMode.Japanese)
                return CollabSyncLanguageMode.Japanese;

            return ResolveAutoLanguageMode();
        }

        static CollabSyncLanguageMode LoadConfiguredLanguageMode()
        {
            var cfg = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
            return cfg != null ? cfg.languageMode : CollabSyncLanguageMode.Auto;
        }

        static CollabSyncLanguageMode ResolveAutoLanguageMode()
        {
            return ResolveAutoLanguage(out var mode, out _, out _)
                ? mode
                : CollabSyncLanguageMode.English;
        }

        static bool ResolveAutoLanguage(out CollabSyncLanguageMode mode, out AutoLanguageSource source, out string sourceDetail)
        {
            var resolution = GetCachedAutoLanguageResolution();
            mode = resolution.mode;
            source = resolution.source;
            sourceDetail = resolution.detail;
            return source != AutoLanguageSource.None;
        }

        static AutoLanguageResolution GetCachedAutoLanguageResolution()
        {
            lock (s_autoLanguageLock)
            {
                var now = DateTime.UtcNow;
                if (s_hasAutoLanguageCache && now - s_autoLanguageCachedAtUtc <= s_autoLanguageCacheDuration)
                    return s_cachedAutoLanguage;

                s_cachedAutoLanguage = DetectAutoLanguageResolution();
                s_autoLanguageCachedAtUtc = now;
                s_hasAutoLanguageCache = true;
                return s_cachedAutoLanguage;
            }
        }

        static AutoLanguageResolution DetectAutoLanguageResolution()
        {
            if (TryGetMacPreferredLanguageMode(out var macMode, out var macDetail))
                return new AutoLanguageResolution(macMode, AutoLanguageSource.MacUserDefaults, macDetail);

            if (TryGetEnvironmentLanguageMode(out var environmentMode, out var environmentDetail))
                return new AutoLanguageResolution(environmentMode, AutoLanguageSource.EnvironmentVariable, environmentDetail);

            if (TryGetCultureLanguageMode(CultureInfo.CurrentUICulture, out var currentUiMode))
                return new AutoLanguageResolution(currentUiMode, AutoLanguageSource.CurrentUICulture, GetCultureSourceDetail(CultureInfo.CurrentUICulture));

            if (TryGetCultureLanguageMode(CultureInfo.InstalledUICulture, out var installedUiMode))
                return new AutoLanguageResolution(installedUiMode, AutoLanguageSource.InstalledUICulture, GetCultureSourceDetail(CultureInfo.InstalledUICulture));

            if (TryGetCultureLanguageMode(CultureInfo.CurrentCulture, out var currentCultureMode))
                return new AutoLanguageResolution(currentCultureMode, AutoLanguageSource.CurrentCulture, GetCultureSourceDetail(CultureInfo.CurrentCulture));

            return new AutoLanguageResolution(CollabSyncLanguageMode.English, AutoLanguageSource.None, "");
        }

        static bool TryGetCultureLanguageMode(CultureInfo culture, out CollabSyncLanguageMode mode)
        {
            mode = CollabSyncLanguageMode.English;
            if (culture == null || Equals(culture, CultureInfo.InvariantCulture))
                return false;

            var code = culture?.TwoLetterISOLanguageName;
            if (string.IsNullOrEmpty(code) || string.Equals(code, "iv", StringComparison.OrdinalIgnoreCase))
                return false;

            mode = string.Equals(code, "ja", StringComparison.OrdinalIgnoreCase)
                ? CollabSyncLanguageMode.Japanese
                : CollabSyncLanguageMode.English;
            return true;
        }

        static string GetCultureSourceDetail(CultureInfo culture)
        {
            if (culture == null)
                return "";

            return !string.IsNullOrEmpty(culture.Name)
                ? culture.Name
                : culture.TwoLetterISOLanguageName;
        }

        static bool TryGetEnvironmentLanguageMode(out CollabSyncLanguageMode mode, out string detail)
        {
            foreach (var variableName in new[] { "LC_ALL", "LC_MESSAGES", "LANGUAGE", "LANG" })
            {
                var value = Environment.GetEnvironmentVariable(variableName);
                if (!TryParseLanguageSetting(value, out mode, out detail))
                    continue;

                detail = variableName + "=" + detail;
                return true;
            }

            mode = CollabSyncLanguageMode.English;
            detail = "";
            return false;
        }

        static bool TryGetMacPreferredLanguageMode(out CollabSyncLanguageMode mode, out string detail)
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.OSXPlayer)
            {
                mode = CollabSyncLanguageMode.English;
                detail = "";
                return false;
            }

            if (TryReadProcessOutput("/usr/bin/defaults", "read -g AppleLanguages", out var languagesOutput)
                && TryExtractMacLanguageToken(languagesOutput, out var languageToken)
                && TryParseLanguageSetting(languageToken, out mode, out detail))
            {
                return true;
            }

            if (TryReadProcessOutput("/usr/bin/defaults", "read -g AppleLocale", out var localeOutput)
                && TryParseLanguageSetting(localeOutput, out mode, out detail))
            {
                return true;
            }

            mode = CollabSyncLanguageMode.English;
            detail = "";
            return false;
        }

        static bool TryReadProcessOutput(string fileName, string arguments, out string output)
        {
            output = "";
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                if (!process.Start())
                    return false;

                output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(1000);
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch { }
                    return false;
                }

                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }

        static bool TryExtractMacLanguageToken(string output, out string token)
        {
            token = "";
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var match = Regex.Match(output, "\"([^\"]+)\"");
            if (match.Success)
            {
                token = match.Groups[1].Value;
                return true;
            }

            return TryParseLanguageSetting(output, out _, out token);
        }

        static bool TryParseLanguageSetting(string value, out CollabSyncLanguageMode mode, out string detail)
        {
            mode = CollabSyncLanguageMode.English;
            detail = "";

            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (var token in SplitLanguageSetting(value))
            {
                if (TryMapLanguageToken(token, out mode))
                {
                    detail = token;
                    return true;
                }
            }

            return false;
        }

        static IEnumerable<string> SplitLanguageSetting(string value)
        {
            var trimmed = value.Trim();
            if (string.IsNullOrEmpty(trimmed))
                yield break;

            var parts = trimmed
                .Split(new[] { '\n', '\r', ',', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var token = part.Trim().Trim('"', '(', ')');
                if (string.IsNullOrEmpty(token))
                    continue;
                if (string.Equals(token, "C", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token, "C.UTF-8", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token, "POSIX", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return token;
            }
        }

        static bool TryMapLanguageToken(string token, out CollabSyncLanguageMode mode)
        {
            mode = CollabSyncLanguageMode.English;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var normalized = token.Trim();
            var separatorIndex = normalized.IndexOfAny(new[] { '.', '@' });
            if (separatorIndex >= 0)
                normalized = normalized.Substring(0, separatorIndex);

            normalized = normalized.Replace('_', '-');
            if (normalized.Length < 2)
                return false;

            var languageCode = normalized.Substring(0, 2);
            if (!Regex.IsMatch(languageCode, "^[A-Za-z]{2}$"))
                return false;

            mode = string.Equals(languageCode, "ja", StringComparison.OrdinalIgnoreCase)
                ? CollabSyncLanguageMode.Japanese
                : CollabSyncLanguageMode.English;
            return true;
        }

        static string DescribeMode(CollabSyncLanguageMode mode)
        {
            return mode == CollabSyncLanguageMode.Japanese
                ? T("Japanese", "日本語")
                : T("English", "英語");
        }

#if UNITY_EDITOR
        static bool IsAffectedUnity6000EditorVersion(string unityVersion)
        {
            if (string.IsNullOrEmpty(unityVersion))
                return false;

            var suffixIndex = unityVersion.IndexOfAny(new[] { 'a', 'b', 'f', 'p' });
            var numericPart = suffixIndex >= 0 ? unityVersion.Substring(0, suffixIndex) : unityVersion;
            var parts = numericPart.Split('.');
            if (parts.Length < 3)
                return false;

            if (!int.TryParse(parts[0], out var major)
                || !int.TryParse(parts[1], out var minor)
                || !int.TryParse(parts[2], out var patch))
                return false;

            return major == 6000 && minor == 0 && patch < 69;
        }
#endif

        public static string T(string english, string japanese)
        {
            return UseJapanese && !string.IsNullOrEmpty(japanese) ? japanese : english;
        }

        public static string F(string english, string japanese, params object[] args)
        {
            return string.Format(T(english, japanese), args);
        }
    }
}
