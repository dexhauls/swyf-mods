using HarmonyLib;
using UnityEngine;

namespace Emotes
{
    // Extra emotes: the game only knows clips from its own list. Teaching FindClip the extra ones lets a modded host
    // accept and broadcast them, and modded clients play them. Players without the mod simply don't see them.
    [HarmonyPatch(typeof(PlayerEmoterNetworking), "FindClip")]
    internal static class FindClipPatch
    {
        private static void Postfix(string name, ref AnimationClip __result)
        {
            if (__result != null) return;
            __result = EmoteCatalog.FindExtra(name);
            if (__result == null && DanceLibrary.Find(name) != null) __result = EmoteCatalog.DancePlaceholder();
        }
    }

    // When the game starts an emote whose name is one of our dances, the dance driver takes over the skeleton.
    [HarmonyPatch(typeof(PlayerEmoterNetworking), "UserCode_RpcPlayEmote__String")]
    internal static class DanceStartPatch
    {
        private static void Postfix(PlayerEmoterNetworking __instance, string clipName)
        {
            var dance = DanceLibrary.Find(clipName);
            if (dance != null && __instance.IsEmoting) DanceDriver.Begin(__instance, dance);
            EmoteLoop.Started(__instance, clipName);
            MovableDances.Forget();
            MovableDances.Started(__instance, clipName);
            if (__instance.IsEmoting) EmoteAnchors.Begin(__instance);
        }
    }

    // B no longer scratches your butt (it's on the wheel now). The wheel reads the same keybind with Held(),
    // so it follows whatever key the player bound "ButtScratch" to.
    // While the wheel is open the mouse picks an emote instead of turning the camera.
    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Pressed))]
    internal static class ButtScratchKeyPatch
    {
        private static bool Prefix(string map, string action, ref bool __result)
        {
            if (map == "Shortcuts" && action == "ButtScratch" && Plugin.WheelEnabled) { __result = false; return false; }
            if (EmoteWheel.IsOpen && !(map == "Shortcuts" && (action == "PauseMenu" || action == "MonitorEscape"))) { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Vector))]
    internal static class LookWhileWheelPatch
    {
        private static bool Prefix(string map, string action, ref Vector2 __result)
        {
            if (!EmoteWheel.IsOpen || !(map == "Player" && action == "Look")) return true;
            __result = Vector2.zero;
            return false;
        }
    }
}
