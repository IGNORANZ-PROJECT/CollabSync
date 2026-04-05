using System;
using System.Globalization;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncLocalization
    {
        enum AutoLanguageSource
        {
            None,
            CurrentUICulture,
            CurrentCulture,
            ApplicationSystemLanguage
        }

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

            if (source == AutoLanguageSource.CurrentUICulture)
            {
                return F(
                    "Current language: {0} (Auto -> OS UI culture: {1}).",
                    "現在の言語: {0}（自動設定 -> OS UI カルチャ: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.CurrentCulture)
            {
                return F(
                    "Current language: {0} (Auto -> OS culture: {1}).",
                    "現在の言語: {0}（自動設定 -> OS カルチャ: {1}）。",
                    resolvedLabel,
                    sourceDetail);
            }

            if (source == AutoLanguageSource.ApplicationSystemLanguage)
            {
                return F(
                    "Current language: {0} (Auto -> Application.systemLanguage: {1}).",
                    "現在の言語: {0}（自動設定 -> Application.systemLanguage: {1}）。",
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
            if (TryGetCultureLanguageMode(CultureInfo.CurrentUICulture, out mode))
            {
                source = AutoLanguageSource.CurrentUICulture;
                sourceDetail = GetCultureSourceDetail(CultureInfo.CurrentUICulture);
                return true;
            }

            if (TryGetCultureLanguageMode(CultureInfo.CurrentCulture, out mode))
            {
                source = AutoLanguageSource.CurrentCulture;
                sourceDetail = GetCultureSourceDetail(CultureInfo.CurrentCulture);
                return true;
            }

            if (TryGetSystemLanguageMode(out mode))
            {
                source = AutoLanguageSource.ApplicationSystemLanguage;
                sourceDetail = Application.systemLanguage.ToString();
                return true;
            }

            mode = CollabSyncLanguageMode.English;
            source = AutoLanguageSource.None;
            sourceDetail = "";
            return false;
        }

        static bool TryGetSystemLanguageMode(out CollabSyncLanguageMode mode)
        {
            mode = CollabSyncLanguageMode.English;
            if (Application.systemLanguage == SystemLanguage.Unknown)
                return false;

            mode = Application.systemLanguage == SystemLanguage.Japanese
                ? CollabSyncLanguageMode.Japanese
                : CollabSyncLanguageMode.English;
            return true;
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
