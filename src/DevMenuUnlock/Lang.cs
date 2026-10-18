namespace DevMenuUnlock
{
    /// <summary>Menu language setting: follow the game, or force Czech / English.</summary>
    internal enum MenuLanguage
    {
        Auto,
        Czech,
        English
    }

    /// <summary>Game language → Czech or English (any other language falls back to English).</summary>
    internal static class Lang
    {
        public static bool Czech
        {
            get
            {
                var setting = Plugin.Language != null ? Plugin.Language.Value : MenuLanguage.Auto;
                if (setting == MenuLanguage.Czech) return true;
                if (setting == MenuLanguage.English) return false;

                string c = null;
                try { c = LanguageManager.EffectiveCode; } catch { }
                c = (c ?? "en").ToLowerInvariant();
                return c.StartsWith("cs") || c.StartsWith("sk");
            }
        }

        public static string T(string cs, string en) => Czech ? cs : en;
    }
}
