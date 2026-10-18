using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace StarterMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.startermod";
        public const string PluginName = "Starter Mod";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // Generates BepInEx\config\dex.swyf.startermod.cfg on first run.
            Enabled = Config.Bind("General", "Enabled", true, "Turns the mod on or off.");

            // Applies every [HarmonyPatch] class in this assembly (none yet, see Patches.cs).
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
