namespace SwyfModManager
{
    /// <summary>
    /// Player-visible text follows the game's language: Czech when the game is set to Czech or Slovak,
    /// English otherwise. Read by reflection, because the manager loads before the game's assemblies and
    /// must not reference them. Logs stay English.
    /// </summary>
    internal static class Lang
    {
        private static System.Reflection.PropertyInfo _code;

        public static bool Czech
        {
            get
            {
                try
                {
                    // EffectiveCode loads the saved choice from PlayerPrefs on first use and resolves "auto"
                    // to the system language, so it works even before the game's menus are up.
                    if (_code == null) _code = System.Type.GetType("LanguageManager, ScriptsAssDef")?.GetProperty("EffectiveCode");
                    var c = ((_code?.GetValue(null) as string) ?? "en").ToLowerInvariant();
                    return c.StartsWith("cs") || c.StartsWith("sk");
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string T(string cs, string en) => Czech ? cs : en;
    }
}
