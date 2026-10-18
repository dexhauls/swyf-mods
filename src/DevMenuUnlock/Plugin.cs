using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DevMenuUnlock
{
    /// <summary>
    /// Unlocks the game's built-in developer menu (NetworkingDevMenu) while you are hosting.
    /// In other people's lobbies nothing changes: their server would reject the commands anyway,
    /// and client-side cheats like 5x speed shouldn't work there.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.devmenuunlock";
        public const string PluginName = "Dev Menu Unlock";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ModernUi;
        internal static ConfigEntry<MenuLanguage> Language;
        internal static ConfigEntry<float> UiScale;
        internal static ConfigEntry<string> ExpandedSections;
        private static ConfigEntry<Key> _toggleKey;

        internal static string ToggleKeyName => _toggleKey.Value.ToString();

        private static readonly AccessTools.FieldRef<NetworkingDevMenu, bool> MenuVisible =
            AccessTools.FieldRefAccess<NetworkingDevMenu, bool>("isMenuVisible");

        private NetworkingDevMenu _menu;
        private bool _wasUnlocked;

        /// <summary>True while this game instance runs the server, i.e. you are the host.</summary>
        internal static bool IsUnlocked => Enabled.Value && NetworkServer.active;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Unlock the developer menu while you are hosting.");
            _toggleKey = Config.Bind("General", "ToggleKey", Key.F8,
                "Key that opens/closes the developer menu (the game's own shortcut works too).");
            ModernUi = Config.Bind("Look", "ModernUi", true, "Draw the menu with the modern look. false = the game's original menu.");
            Language = Config.Bind("Look", "Language", MenuLanguage.Auto,
                "Menu language. Auto = follow the game's language (Czech for Czech/Slovak, English otherwise), Czech, English.");
            UiScale = Config.Bind("Look", "UiScale", 0f, "Menu scale. 0 = automatic by screen height (1.0 at 1080p).");
            ExpandedSections = Config.Bind("Look", "ExpandedSections", "Dev Menu Active,Daukey Menu",
                "Sections that are expanded (game's English names, comma separated). Updated when you click a section.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Host a lobby and press {_toggleKey.Value}.");
        }

        private Harmony _harmony;

        // The mod manager can turn the mod off while the game runs: close the menu and remove the patches.
        private void OnDestroy()
        {
            if (_menu != null) MenuVisible(_menu) = false;
            ModernDevMenu.InMenu = false;
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (IsUnlocked != _wasUnlocked)
            {
                _wasUnlocked = IsUnlocked;
                Log.LogInfo(_wasUnlocked
                    ? $"You are hosting: developer menu unlocked ({_toggleKey.Value})."
                    : "Not hosting anymore: developer menu locked.");
            }

            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard[_toggleKey.Value].wasPressedThisFrame) return;

            if (!IsUnlocked)
            {
                Log.LogInfo("Developer menu is only available while you are hosting.");
                return;
            }

            if (_menu == null) _menu = FindAnyObjectByType<NetworkingDevMenu>();
            if (_menu == null)
            {
                Log.LogWarning("NetworkingDevMenu not found in the current scene.");
                return;
            }

            MenuVisible(_menu) = !MenuVisible(_menu);
            Log.LogInfo(MenuVisible(_menu) ? "Developer menu opened." : "Developer menu closed.");
        }

    }

    [HarmonyPatch(typeof(NetworkingDevMenu), nameof(NetworkingDevMenu.IsDeveloper))]
    internal static class IsDeveloperPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (Plugin.IsUnlocked) __result = true;
        }
    }
}
