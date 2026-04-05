using System;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public static class CollabSyncLocalization
    {
        public static bool UseJapanese => ResolveLanguageMode() == CollabSyncLanguageMode.Japanese;

        static CollabSyncLanguageMode ResolveLanguageMode()
        {
            var configuredMode = LoadConfiguredLanguageMode();
            if (configuredMode == CollabSyncLanguageMode.English)
                return CollabSyncLanguageMode.English;
            if (configuredMode == CollabSyncLanguageMode.Japanese)
                return CollabSyncLanguageMode.Japanese;

            return Application.systemLanguage == SystemLanguage.Japanese
                ? CollabSyncLanguageMode.Japanese
                : CollabSyncLanguageMode.English;
        }

        static CollabSyncLanguageMode LoadConfiguredLanguageMode()
        {
#if UNITY_EDITOR
            var cfg = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
#else
            var cfg = Resources.Load<CollabSyncConfig>("CollabSyncConfig");
#endif
            return cfg != null ? cfg.languageMode : CollabSyncLanguageMode.Auto;
        }

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
