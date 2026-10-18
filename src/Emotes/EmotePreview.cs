using System;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Plays an emote on your own character while you browse the locker in Employee setup. It runs only on your
    /// machine (nothing is sent to the server), so nobody else sees it; the music plays only for you too.
    /// </summary>
    internal static class EmotePreview
    {
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, OneShotEmotePlayer> Playback =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, OneShotEmotePlayer>("playback");
        private static readonly Action<PlayerEmoterNetworking> RefreshProceduralSuppression =
            AccessTools.MethodDelegate<Action<PlayerEmoterNetworking>>(AccessTools.Method(typeof(PlayerEmoterNetworking), "RefreshProceduralSuppression"));
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, PlayerRagdollManager> Ragdoll =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, PlayerRagdollManager>("ragdoll");

        private static PlayerEmoterNetworking _emoter;

        /// <summary>True while a preview runs on this player (the emoter must not cancel it for "input blocked").</summary>
        public static bool Active(PlayerEmoterNetworking emoter) => _emoter != null && emoter == _emoter && emoter.IsEmoting;

        /// <summary>Starts the preview; returns false for emotes that can't be previewed (fart, butt scratch...).</summary>
        public static bool Play(EmoteEntry e)
        {
            var me = NetworkClient.localPlayer;
            var emoter = me != null ? me.GetComponent<PlayerEmoterNetworking>() : null;
            if (emoter == null || e == null) return false;

            AnimationClip clip;
            string name;
            switch (e.Kind)
            {
                case EmoteKind.Catalog: clip = EmoteCatalog.FindCatalog(e.Clip); name = e.Clip; break;
                case EmoteKind.Extra: clip = EmoteCatalog.FindExtra(e.Clip); name = e.Clip; break;
                case EmoteKind.Dance: clip = EmoteCatalog.DancePlaceholder(); name = e.Clip; break;
                default: return false;
            }
            var playback = Playback(emoter);
            if (clip == null || playback == null) return false;

            Stop();
            _emoter = emoter;
            var ragdoll = Ragdoll(emoter);
            if (ragdoll != null) ragdoll.SetProceduralAnimatorsSuppressedForEmote(true);
            playback.Play(clip);
            RefreshProceduralSuppression(emoter);
            if (!emoter.IsEmoting) { _emoter = null; return false; }

            var dance = DanceLibrary.Find(name);
            if (dance != null) DanceDriver.Begin(emoter, dance);
            EmoteLoop.Started(emoter, name);
            EmoteAnchors.Begin(emoter);
            return true;
        }

        public static void Stop()
        {
            var emoter = _emoter;
            _emoter = null;
            if (emoter == null) return;
            Playback(emoter)?.Stop();
            RefreshProceduralSuppression(emoter);
        }
    }

    // Employee setup closed: end the preview.
    [HarmonyPatch(typeof(Metater.RandomNpcSkinPreviewUi), "OnDestroy")]
    internal static class EmployeeSetupClosedPatch
    {
        private static void Prefix() => EmotePreview.Stop();
    }
}
