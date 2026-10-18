using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ArcadeTest")]

namespace Arcade
{
    /// <summary>
    /// Adds an "Arkáda" app with retro games to the in-game computer (Kolkata OS). Nothing in the game's files
    /// changes: the app definition is injected when the desktop is built, and the window content is drawn by
    /// <see cref="ArcadeView"/>. Works in any lobby, only on your own screen.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.arcade";
        public const string PluginName = "Arkáda";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static string ModFolder => Path.Combine(Paths.GameRootPath, "Mods", "Arcade");

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            OsBridge.IconPath = WriteIcon();
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            // Loaded while the game already shows a desktop (hot load): add the app to it right away.
            OsBridge.ApplyToLiveDesktops(add: true);
            try { LanguageManager.OnLanguageChanged += OnLanguageChanged; }
            catch (System.Exception e) { Log.LogWarning("Could not follow game language changes: " + e.Message); }
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            ArcadeView.TickAll(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            try { LanguageManager.OnLanguageChanged -= OnLanguageChanged; } catch { }
            ArcadeView.DisposeAll();
            OsBridge.ApplyToLiveDesktops(add: false);
            _harmony?.UnpatchSelf();
        }

        /// <summary>Game language changed: re-text open Arkáda windows and the desktop app title.</summary>
        private static void OnLanguageChanged(string code)
        {
            try
            {
                ArcadeView.RefreshAllTexts();
                OsBridge.ApplyLanguage();
                Log.LogInfo($"Language changed to '{code}': Arkáda texts updated.");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Could not apply the new language: " + e.Message);
            }
        }

        /// <summary>The OS loads app icons from files, so the embedded PNG goes next to the mod.</summary>
        private static string WriteIcon()
        {
            try
            {
                using (var s = typeof(Plugin).Assembly.GetManifestResourceStream("Arcade.arcade-icon.png"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    s.Read(bytes, 0, bytes.Length);
                    Directory.CreateDirectory(ModFolder);
                    string path = Path.Combine(ModFolder, "arcade-icon.png");
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) File.WriteAllBytes(path, bytes);
                    return path;
                }
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Could not write the app icon, using a built-in one: " + e.Message);
                return null;
            }
        }
    }
}
