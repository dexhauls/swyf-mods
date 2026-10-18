using System;
using System.Collections.Generic;
using HarmonyLib;
using Metater;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Some dances can be danced while walking (like Fortnite's traversal emotes): moving doesn't cancel them and
    /// the dancer walks at about half speed, no sprint. Which ones is set in the config (MovableDances).
    /// </summary>
    internal static class MovableDances
    {
        public const float SpeedFactor = 0.55f;
        public const float StrollPerHipHeight = 1.1f;

        private static readonly Dictionary<string, float> Keys = new Dictionary<string, float>(); // key -> speed factor
        private static readonly Dictionary<PlayerEmoterNetworking, string> Current = new Dictionary<PlayerEmoterNetworking, string>();

        public static void Configure(string list)
        {
            Keys.Clear();
            // "Smeeze:0.18" = this dance walks at 18 % of walking speed; no number = SpeedFactor.
            foreach (var part in (list ?? "").Split(','))
            {
                var bits = part.Split(':');
                string k = DanceMusic.Key(bits[0].Trim());
                float factor = SpeedFactor;
                if (bits.Length > 1) float.TryParse(bits[1].Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out factor);
                if (k.Length > 0) Keys[k] = Mathf.Clamp(factor, 0.05f, 1.5f);
            }
        }

        public static bool IsMovable(string clipOrDanceId) => Factor(clipOrDanceId) > 0f;

        /// <summary>Walking speed factor for this dance, 0 = can't walk in it.</summary>
        public static float Factor(string clipOrDanceId)
        {
            if (string.IsNullOrEmpty(clipOrDanceId)) return 0f;
            if (Keys.TryGetValue(DanceMusic.Key(clipOrDanceId), out var f)) return f;
            var d = DanceLibrary.Find(clipOrDanceId);
            return d != null && Keys.TryGetValue(DanceMusic.Key(d.Label), out f) ? f : 0f;
        }

        public static float CurrentFactor(PlayerEmoterNetworking emoter) =>
            Current.TryGetValue(emoter, out var clip) ? Factor(clip) : SpeedFactor;

        public static void Started(PlayerEmoterNetworking emoter, string clip) => Current[emoter] = clip;

        /// <summary>True while this player is doing a dance that allows walking.</summary>
        public static bool Active(PlayerEmoterNetworking emoter)
        {
            if (emoter == null || !emoter.IsEmoting) return false;
            return Current.TryGetValue(emoter, out var clip) && IsMovable(clip);
        }

        public static bool LocalActive()
        {
            var me = Mirror.NetworkClient.localPlayer;
            return me != null && Active(me.GetComponent<PlayerEmoterNetworking>());
        }

        public static void Forget()
        {
            var dead = new List<PlayerEmoterNetworking>();
            foreach (var kv in Current) if (kv.Key == null || !kv.Key.IsEmoting) dead.Add(kv.Key);
            foreach (var k in dead) Current.Remove(k);
        }
    }

    // The game's emoter cancels on any movement. For walkable dances run the same Update minus that check.
    [HarmonyPatch(typeof(PlayerEmoterNetworking), "Update")]
    internal static class WalkWhileDancingPatch
    {
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, Vector3> PreviousPosition =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, Vector3>("previousPosition");
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, OneShotEmotePlayer> Playback =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, OneShotEmotePlayer>("playback");
        private static readonly AccessTools.FieldRef<PlayerEmoterNetworking, MetaPlayerController> Movement =
            AccessTools.FieldRefAccess<PlayerEmoterNetworking, MetaPlayerController>("movement");
        private static readonly Func<PlayerEmoterNetworking, bool> IsPhysicallyBlocked =
            AccessTools.MethodDelegate<Func<PlayerEmoterNetworking, bool>>(AccessTools.Method(typeof(PlayerEmoterNetworking), "IsPhysicallyBlocked"));
        private static readonly Func<PlayerEmoterNetworking, bool> IsLocalInputBlocked =
            AccessTools.MethodDelegate<Func<PlayerEmoterNetworking, bool>>(AccessTools.Method(typeof(PlayerEmoterNetworking), "IsLocalInputBlocked"));
        private static readonly Action<PlayerEmoterNetworking, bool> CancelPlayback =
            AccessTools.MethodDelegate<Action<PlayerEmoterNetworking, bool>>(AccessTools.Method(typeof(PlayerEmoterNetworking), "CancelPlayback"));
        private static readonly Action<PlayerEmoterNetworking> RefreshProceduralSuppression =
            AccessTools.MethodDelegate<Action<PlayerEmoterNetworking>>(AccessTools.Method(typeof(PlayerEmoterNetworking), "RefreshProceduralSuppression"));

        private static bool Prefix(PlayerEmoterNetworking __instance)
        {
            if (EmotePreview.Active(__instance))
            {
                // Locker preview: the player is "input blocked" by the Employee setup UI, which would cancel it.
                PreviousPosition(__instance) = __instance.transform.position;
                Playback(__instance)?.Tick(Time.unscaledDeltaTime);
                RefreshProceduralSuppression(__instance);
                return false;
            }
            if (!MovableDances.Active(__instance)) return true;
            PreviousPosition(__instance) = __instance.transform.position;
            bool local = __instance.isLocalPlayer;
            var movement = Movement(__instance);
            bool airborne = local && movement != null && !movement.isGrounded;
            if (IsPhysicallyBlocked(__instance) || (local && IsLocalInputBlocked(__instance)) || airborne)
                CancelPlayback(__instance, local);
            Playback(__instance)?.Tick(Time.unscaledDeltaTime);
            RefreshProceduralSuppression(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(MetaPlayerController), nameof(MetaPlayerController.GetTargetSpeed))]
    internal static class DanceWalkSpeedPatch
    {
        private static readonly AccessTools.FieldRef<MetaPlayerController, MetaPlayerConfig> Config =
            AccessTools.FieldRefAccess<MetaPlayerController, MetaPlayerConfig>("config");

        private static float _nextLog, _logTime;
        private static Vector3 _logPos;

        private static void Postfix(MetaPlayerController __instance, ref float __result)
        {
            if (__result <= 0f) return;
            var emoter = __instance.GetComponent<PlayerEmoterNetworking>();
            if (emoter == null || !emoter.isLocalPlayer || !MovableDances.Active(emoter)) return;
            var cfg = Config(__instance);
            if (cfg == null) return;
            // On skates: cruise, and each push of the dance drives a little harder (SkatePhysicsPatch does the rest).
            if (DanceDriver.LocalSkating(out _, out float push))
            {
                __result = Mathf.Min(cfg.walkSpeed, SkatePhysicsPatch.Cruise + SkatePhysicsPatch.PushExtra * push);
                return;
            }
            // Fortnite's traversal emotes stroll at roughly 1.1 hip-heights per second; size it to this character.
            // A per-dance number scales that (0.55 = normal, so "Smeeze:0.18" walks a third as fast).
            float stroll = DanceDriver.HipsHeight(emoter) * MovableDances.StrollPerHipHeight;
            float rel = MovableDances.CurrentFactor(emoter) / MovableDances.SpeedFactor;
            // Pace from the dance's own feet right now: where it holds a pose or a leg up, the dancer slows down
            // to a stop instead of floating; never faster than a brisk stroll.
            float gait = DanceDriver.GaitSpeed(emoter);
            float fixedSpeed = DanceDriver.FixedWalkSpeed(emoter);
            if (fixedSpeed > 0f)
            {
                __result = Mathf.Min(__result, cfg.walkSpeed, fixedSpeed);
                return;
            }
            bool follow = DanceDriver.FollowsDance(emoter) || DanceDriver.HopPaced(emoter);
            float pace = follow ? gait : gait > 0f || DanceDriver.HasGait(emoter) ? Mathf.Min(gait, stroll * 1.3f) : stroll;
            float before = __result;
            __result = Mathf.Min(__result, cfg.walkSpeed, pace * rel);
            // The speed follows each push of the dance, so the body must take it at once (see SnapToDanceSpeedPatch).
            if (follow)
            {
                SnapToDanceSpeedPatch.Frame = Time.frameCount;
                SnapToDanceSpeedPatch.Momentum = DanceDriver.Momentum(emoter);
            }
            // How fast the body really moved since the last report (to check the speed lands as asked).
            var p = __instance.transform.position;
            if (Time.unscaledTime > _nextLog)
            {
                float dtLog = Time.unscaledTime - _logTime;
                var d = p - _logPos;
                d.y = 0f;
                string moved = dtLog > 0f && dtLog < 10f ? $", moved {d.magnitude / dtLog:0.00}/s" : "";
                _nextLog = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo($"Dance walk: {__result:0.00} (normal walk {cfg.walkSpeed:0.00}, asked {before:0.00}, hips {DanceDriver.HipsHeight(emoter):0.00}, gait {gait:0.00}{(DanceDriver.LastInPlace ? " sideways" : " forward")}{moved})");
                _logPos = p;
                _logTime = Time.unscaledTime;
            }
        }
    }

    /// <summary>
    /// The game eases the player's speed towards the target (about a fifth of a second). While the walking speed follows
    /// a dance's pushes frame by frame that would smear every push, so for those frames the speed is taken at once.
    /// </summary>
    [HarmonyPatch]
    internal static class SnapToDanceSpeedPatch
    {
        public static int Frame = -1;
        public static float Momentum;   // > 0: ease at this rate instead (skating glide), stopping included

        private static bool Active => Frame == Time.frameCount && Time.deltaTime > 0f;

        [HarmonyPatch(typeof(MetaPlayerConfig), nameof(MetaPlayerConfig.acceleration), MethodType.Getter)]
        [HarmonyPostfix]
        private static void Acceleration(ref float __result)
        {
            if (Active) __result = Momentum > 0f ? Momentum : 1f / Time.deltaTime;
        }

        [HarmonyPatch(typeof(MetaPlayerConfig), nameof(MetaPlayerConfig.DecelerationFactor), MethodType.Getter)]
        [HarmonyPostfix]
        private static void Deceleration(ref float __result)
        {
            if (Active) __result = 1f;
        }
    }

    /// <summary>
    /// Skating physics for the local player (a dance with skates): the speed carries on, slowed only by the wheels'
    /// rolling (a long glide when you let go); the wheels grip sideways, so the way you roll turns with the skates;
    /// holding a direction pushes towards cruising speed, holding against the roll brakes; a wall takes the speed away.
    /// Works on the game's own movement: before it moves the player the remembered velocity is skated on, and its
    /// easing becomes the push.
    /// </summary>
    [HarmonyPatch]
    internal static class SkatePhysicsPatch
    {
        public const float Cruise = 2.9f, PushExtra = 0.9f;    // world units/s (walking is 5)
        private const float Rolling = 0.16f, Grip = 7f, PushAccel = 1.1f, Brake = 2.6f;

        private static readonly AccessTools.FieldRef<MetaPlayerController, Vector3> LastVelocity =
            AccessTools.FieldRefAccess<MetaPlayerController, Vector3>("lastHorizontalVelocity");

        private static MetaPlayerController _skater;
        private static int _frame = -1;

        private static bool Active => _frame == Time.frameCount && _skater != null;

        [HarmonyPatch(typeof(MetaPlayerController), "Move")]
        [HarmonyPrefix]
        private static void BeforeMove(MetaPlayerController __instance)
        {
            var emoter = __instance.GetComponent<PlayerEmoterNetworking>();
            if (emoter == null || !emoter.isLocalPlayer || !DanceDriver.LocalSkating(out var facing, out _)) return;
            float dt = Time.deltaTime;
            Vector3 v = LastVelocity(__instance);
            float along = Vector3.Dot(v, facing);
            Vector3 side = v - facing * along;
            along *= Mathf.Exp(-Rolling * dt);
            side *= Mathf.Exp(-Grip * dt);
            LastVelocity(__instance) = facing * along + side;
            _skater = __instance;
            _frame = Time.frameCount;
        }

        [HarmonyPatch(typeof(MetaPlayerController), "Move")]
        [HarmonyPostfix]
        private static void AfterMove(MetaPlayerController __instance)
        {
            if (!Active || __instance != _skater) return;
            // Ran into something: the speed is what the player really moved, not what it wanted to.
            Vector3 real = __instance.CharacterController.velocity;
            real.y = 0f;
            Vector3 wanted = LastVelocity(__instance);
            if (real.magnitude < wanted.magnitude * 0.7f) LastVelocity(__instance) = real;
        }

        [HarmonyPatch(typeof(MetaPlayerConfig), nameof(MetaPlayerConfig.acceleration), MethodType.Getter)]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Acceleration(ref float __result)
        {
            if (Active) __result = _skater.isMoving ? PushAccel : 0f;   // let go: just roll (friction is above)
        }

        [HarmonyPatch(typeof(MetaPlayerConfig), nameof(MetaPlayerConfig.DecelerationFactor), MethodType.Getter)]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Deceleration(ref float __result)
        {
            if (Active) __result = Brake;
        }
    }
}
