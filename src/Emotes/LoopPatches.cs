using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// The game plays every emote once and then returns to idle. Dances loop here until the player moves
    /// (movement already cancels emotes in the game's own code). Each client loops on its own, so players without
    /// the mod still see a dance play once.
    /// </summary>
    internal static class EmoteLoop
    {
        private sealed class State
        {
            public bool Loop;
            public float Period;
        }

        private static readonly ConditionalWeakTable<OneShotEmotePlayer, State> States = new ConditionalWeakTable<OneShotEmotePlayer, State>();
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, OneShotEmotePlayer> Playback =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, OneShotEmotePlayer>("playback");
        internal static readonly AccessTools.FieldRef<OneShotEmotePlayer, float> Remaining =
            AccessTools.FieldRefAccess<OneShotEmotePlayer, float>("remaining");
        internal static readonly AccessTools.FieldRef<OneShotEmotePlayer, float> ReturnRemaining =
            AccessTools.FieldRefAccess<OneShotEmotePlayer, float>("returnRemaining");
        internal static readonly AccessTools.FieldRef<OneShotEmotePlayer, int> EmoteHash =
            AccessTools.FieldRefAccess<OneShotEmotePlayer, int>("emoteHash");
        private static readonly System.Reflection.FieldInfo Targets = AccessTools.Field(typeof(OneShotEmotePlayer), "targets");
        private static System.Reflection.FieldInfo _targetAnimator;

        /// <summary>Dances loop: our own dances and everything on the wheel's dance page.</summary>
        public static bool ShouldLoop(string clipName) =>
            DanceLibrary.Find(clipName) != null || EmoteCatalog.IsDancePageClip(clipName);

        public static void Started(PlayerEmoterNetworking emoter, string clipName)
        {
            var playback = Playback(emoter);
            if (playback == null || !playback.IsPlaying) return;
            var s = States.GetOrCreateValue(playback);
            s.Loop = Plugin.LoopDances && ShouldLoop(clipName);
            s.Period = Remaining(playback); // Play() set it to the clip length
        }

        /// <summary>Called before the game's Tick: when a looping emote is about to end, start it again.</summary>
        public static void BeforeTick(OneShotEmotePlayer playback, float dt)
        {
            if (!States.TryGetValue(playback, out var s) || !s.Loop || s.Period <= 0.05f) return;
            if (!playback.IsPlaying) { s.Loop = false; return; }
            if (ReturnRemaining(playback) > 0f || Remaining(playback) - Mathf.Max(0f, dt) > 0f) return;
            Remaining(playback) += s.Period;
            int hash = EmoteHash(playback);
            if (!(Targets.GetValue(playback) is IList targets)) return;
            foreach (var t in targets)
            {
                if (_targetAnimator == null) _targetAnimator = AccessTools.Field(t.GetType(), "Animator");
                if (_targetAnimator.GetValue(t) is Animator a && a != null && a.isActiveAndEnabled) a.Play(hash, 0, 0f);
            }
        }

        public static void Stopped(OneShotEmotePlayer playback)
        {
            if (States.TryGetValue(playback, out var s)) s.Loop = false;
        }
    }

    [HarmonyPatch(typeof(OneShotEmotePlayer), nameof(OneShotEmotePlayer.Tick))]
    internal static class LoopTickPatch
    {
        private static void Prefix(OneShotEmotePlayer __instance, float deltaTime) => EmoteLoop.BeforeTick(__instance, deltaTime);
    }

    [HarmonyPatch(typeof(OneShotEmotePlayer), nameof(OneShotEmotePlayer.Stop))]
    internal static class LoopStopPatch
    {
        private static void Prefix(OneShotEmotePlayer __instance) => EmoteLoop.Stopped(__instance);
    }
}
