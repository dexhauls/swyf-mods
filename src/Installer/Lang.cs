using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace SwyfInstaller
{
    /// <summary>
    /// The hub speaks the game's language: Czech when the game is set to Czech or Slovak, English otherwise.
    /// The hub runs outside the game, so it reads the game's saved PlayerPrefs: Unity keeps them in
    /// HKCU\Software\&lt;company&gt;\&lt;product&gt; (lines 1 and 2 of &lt;game&gt;_Data\app.info). The game's
    /// LanguageManager saves "LanguageManager.LanguageCode" (registry value "LanguageManager.LanguageCode_h&lt;hash&gt;",
    /// REG_BINARY with a null-terminated UTF-8 string such as "cs", "en" or "auto"). "auto" or nothing saved
    /// = the Windows UI language.
    /// </summary>
    internal static class Lang
    {
        private const string LanguageValuePrefix = "LanguageManager.LanguageCode";
        private const string GameName = "Scam With Your Friends";

        private static string _keyPath;     // registry path of the game's PlayerPrefs, once found
        private static string _keyGameDir;  // game folder _keyPath was resolved for

        public static bool Czech { get; private set; } = Detect(null);

        public static string T(string cs, string en) => Czech ? cs : en;

        /// <summary>A mod.json text: the English variant when the UI is English and it exists, otherwise the Czech one.</summary>
        public static string Pick(string cs, string en) => !Czech && !string.IsNullOrEmpty(en) ? en : cs;

        public static List<string> Pick(List<string> cs, List<string> en) => !Czech && en != null && en.Count > 0 ? en : cs;

        public static CultureInfo Culture => CultureInfo.GetCultureInfo(Czech ? "cs-CZ" : "en-US");

        /// <summary>Reads the game's language again. True when the UI language changed.</summary>
        public static bool Refresh(string gameDir)
        {
            var czech = Detect(gameDir);
            if (czech == Czech) return false;
            Czech = czech;
            return true;
        }

        private static bool Detect(string gameDir)
        {
            string code = null;
            try
            {
                code = ReadGameLanguage(gameDir);
            }
            catch
            {
                // No access to the registry or garbage in it: fall back to Windows.
            }
            if (string.IsNullOrWhiteSpace(code) || code.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                code = CultureInfo.CurrentUICulture.Name;
            code = code.Trim().ToLowerInvariant();
            return code.StartsWith("cs") || code.StartsWith("sk") || code.StartsWith("czech") || code.StartsWith("slovak");
        }

        private static string ReadGameLanguage(string gameDir)
        {
            using (var key = OpenPrefsKey(gameDir))
            {
                if (key == null) return null;
                var name = key.GetValueNames().FirstOrDefault(n => n.StartsWith(LanguageValuePrefix, StringComparison.Ordinal) &&
                                                                   (n.Length == LanguageValuePrefix.Length || n[LanguageValuePrefix.Length] == '_'));
                if (name == null) return null;
                switch (key.GetValue(name))
                {
                    case byte[] bytes:
                        var text = Encoding.UTF8.GetString(bytes);
                        var end = text.IndexOf('\0');
                        return end >= 0 ? text.Substring(0, end) : text;
                    case string s:
                        return s;
                    default:
                        return null;
                }
            }
        }

        private static RegistryKey OpenPrefsKey(string gameDir)
        {
            if (_keyPath != null && (gameDir == null || string.Equals(gameDir, _keyGameDir, StringComparison.OrdinalIgnoreCase)))
            {
                var cached = Registry.CurrentUser.OpenSubKey(_keyPath);
                if (cached != null) return cached;
            }
            _keyPath = FindPrefsKeyPath(gameDir);
            _keyGameDir = gameDir;
            return _keyPath != null ? Registry.CurrentUser.OpenSubKey(_keyPath) : null;
        }

        private static string FindPrefsKeyPath(string gameDir)
        {
            // Company and product name from the game's app.info.
            if (gameDir != null)
            {
                try
                {
                    var info = Path.Combine(gameDir, Path.GetFileNameWithoutExtension(SteamLocator.ExeName) + "_Data", "app.info");
                    if (File.Exists(info))
                    {
                        var lines = File.ReadAllLines(info);
                        if (lines.Length >= 2 && lines[0].Trim().Length > 0 && lines[1].Trim().Length > 0)
                        {
                            var path = $@"Software\{lines[0].Trim()}\{lines[1].Trim()}";
                            using (var key = Registry.CurrentUser.OpenSubKey(path))
                                if (key != null) return path;
                        }
                    }
                }
                catch
                {
                    // fall through to the scan
                }
            }

            // Game folder unknown (or app.info unreadable): look for Software\<anything>\<…Scam With Your Friends…>.
            using (var software = Registry.CurrentUser.OpenSubKey("Software"))
            {
                if (software == null) return null;
                foreach (var company in software.GetSubKeyNames())
                {
                    try
                    {
                        using (var c = software.OpenSubKey(company))
                        {
                            if (c == null) continue;
                            var product = c.GetSubKeyNames().FirstOrDefault(p => p.IndexOf(GameName, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (product != null) return $@"Software\{company}\{product}";
                        }
                    }
                    catch
                    {
                        // a key we may not open
                    }
                }
            }
            return null;
        }
    }
}
