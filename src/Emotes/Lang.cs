namespace Emotes
{
    /// <summary>Game language → Czech or English (any other language falls back to English).</summary>
    internal static class Lang
    {
        public static bool Czech
        {
            get
            {
                string c = null;
                try { c = LanguageManager.EffectiveCode; } catch { }
                c = (c ?? "en").ToLowerInvariant();
                return c.StartsWith("cs") || c.StartsWith("sk");
            }
        }

        public static string T(string cs, string en) => Czech ? cs : en;
    }
}
