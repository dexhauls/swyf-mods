using HarmonyLib;
using Metater;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// The game bobs the view with every step (PlayerAnimBob squashes the object the camera hangs from: it rises with
    /// the step and drops at the next footfall). While you walk in a dance the bob follows the music instead - it drops
    /// on every beat (every other one in very quick songs) - at 40 % of the game's strength. A dance without music
    /// keeps the footstep bob, cut to 30 %. Otherwise the camera is left as the game made it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAnimBob), "GetHeight")]
    internal static class CalmCameraWhileDancingPatch
    {
        private const float BobWhileDancing = 0.3f;
        private const float BeatBob = 0.4f;

        private static void Postfix(PlayerAnimBob __instance, ref float __result)
        {
            if (!MovableDances.LocalActive()) return;
            float mid = Mathf.Lerp(__instance.minHeight, __instance.maxHeight, 0.5f);
            if (DanceDriver.LocalBeat(out float walk, out float phase))
            {
                // Same shape as the game's: rising through the beat, dropping on it (its smoothing rounds the drop).
                float strength = __instance.strength == PlayerAnimStrength.Strong ? 1f : __instance.strength == PlayerAnimStrength.Weak ? 0.5f : 0f;
                float t = 0.5f + (phase - 0.5f) * __instance.footstepsMultiplier * strength * BeatBob * walk;
                __result = Mathf.Lerp(__instance.minHeight, __instance.maxHeight, t);
                return;
            }
            __result = Mathf.Lerp(mid, __result, BobWhileDancing);
        }
    }
}
