using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwyfInstaller
{
    /// <summary>Finds the game's install folder through Steam's registry key and library list.</summary>
    internal static class SteamLocator
    {
        internal const string AppId = "4954910";
        internal const string ExeName = "Scam With Your Friends.exe";

        internal static string FindGame()
        {
            foreach (var library in Libraries())
            {
                var steamapps = Path.Combine(library, "steamapps");
                var manifest = Path.Combine(steamapps, $"appmanifest_{AppId}.acf");
                var installDir = "Scam With Your Friends";
                if (File.Exists(manifest))
                {
                    var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) installDir = m.Groups[1].Value;
                }
                var dir = Path.Combine(steamapps, "common", installDir);
                if (IsGameFolder(dir)) return dir;
            }
            return null;
        }

        internal static bool IsGameFolder(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ExeName));

        private static IEnumerable<string> Libraries()
        {
            var steam = SteamPath();
            if (steam == null) yield break;
            yield return steam;

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.Equals(path.TrimEnd('\\'), steam.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase))
                    yield return path;
            }
        }

        /// <summary>
        /// The game's artwork from Steam's local library cache (present for anyone who owns the game).
        /// Newer Steam keeps it in librarycache\&lt;appid&gt;\&lt;hash&gt;\name, older Steam as &lt;appid&gt;_name.
        /// </summary>
        internal static string FindArtwork(string name)
        {
            var steam = SteamPath();
            if (steam == null) return null;
            var cache = Path.Combine(steam, "appcache", "librarycache");
            var legacy = Path.Combine(cache, $"{AppId}_{name}");
            if (File.Exists(legacy)) return legacy;
            var dir = Path.Combine(cache, AppId);
            if (!Directory.Exists(dir)) return null;
            try
            {
                return Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string SteamPath()
        {
            var candidates = new[]
            {
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
            };
            return candidates.Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p.Replace('/', '\\'))
                .FirstOrDefault(Directory.Exists);
        }
    }
}
