using System;

namespace Performance
{
    /// <summary>Player-visible texts follow the game's language: Czech for a Czech or Slovak game, English otherwise.</summary>
    internal static class Lang
    {
        public static bool Czech
        {
            get
            {
                try
                {
                    string code = (LanguageManager.EffectiveCode ?? "en").ToLowerInvariant();
                    return code.StartsWith("cs") || code.StartsWith("sk");
                }
                catch { return false; }
            }
        }

        public static string T(string cs, string en) => Czech ? cs : en;

        private static Action<string> _handler;

        /// <summary>Calls <paramref name="onChange"/> whenever the player switches the game's language.</summary>
        public static void Watch(Action onChange)
        {
            Unwatch();
            _handler = _ => { try { onChange(); } catch (Exception e) { Plugin.Log.LogWarning("Language refresh: " + e.Message); } };
            try { LanguageManager.OnLanguageChanged += _handler; } catch { }
        }

        public static void Unwatch()
        {
            if (_handler == null) return;
            try { LanguageManager.OnLanguageChanged -= _handler; } catch { }
            _handler = null;
        }
    }
}
