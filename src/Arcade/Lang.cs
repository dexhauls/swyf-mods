using System.Runtime.CompilerServices;

namespace Arcade
{
    /// <summary>Game language → Czech or English (any other language falls back to English).</summary>
    internal static class Lang
    {
        public static bool Czech
        {
            get
            {
                string c = null;
                try { c = Code(); } catch { }
                c = (c ?? "en").ToLowerInvariant();
                return c.StartsWith("cs") || c.StartsWith("sk");
            }
        }

        public static string T(string cs, string en) => Czech ? cs : en;

        // Separate, non-inlined method: outside the game (offline test) ScriptsAssDef.dll is missing, and the
        // resulting load error is raised when this method is compiled, so the caller's try/catch can catch it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Code() => LanguageManager.EffectiveCode;
    }
}
