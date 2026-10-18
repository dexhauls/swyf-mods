using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;

namespace Performance
{
    /// <summary>
    /// Every office computer carries ~15 AudioSources (1400+ in a full office) and Unity updates each enabled one
    /// every frame even when it is silent: ~1 ms of main thread. Idle sources are disabled ("parked") and switched
    /// back on the moment the game plays anything on them, so nothing can be heard differently.
    /// </summary>
    internal static class AudioParking
    {
        private static readonly HashSet<AudioSource> Parked = new HashSet<AudioSource>();
        private static readonly HashSet<AudioSource> Never = new HashSet<AudioSource>(); // paused, video targets, filters
        private static readonly Dictionary<AudioSource, float> Touched = new Dictionary<AudioSource, float>();
        // Only sources the game itself set to playOnAwake = false are parked: switching a playOnAwake source
        // back on would start it, and the getter is stripped from the game, so it can't be read.
        // Sources without a clip are safe too: switched back on, playOnAwake has nothing to play. When the game
        // gives a parked source a clip, it is woken first (clip setter patch), so that stays true.
        private static readonly HashSet<AudioSource> NoAwake = new HashSet<AudioSource>();
        private const string SharedKey = "dex.swyf.performance.noAwake"; // instance ids, survive a hot reload
        private static readonly Dictionary<Type, bool> FilterTypes = new Dictionary<Type, bool>();
        private static List<AudioSource> _all = new List<AudioSource>();
        private static int _cursor;
        private static float _nextRescan;
        private static bool _on;
        private static Harmony _harmony;

        public static int ParkedCount => Parked.Count;
        public static string Stats => $"{Parked.Count} parked of {_all.Count} sources ({NoAwake.Count} known without playOnAwake, {Parked.Count(a => a != null && a.clip == null)} parked have no clip, {Never.Count} excluded)";

        public static void Patch(Harmony h)
        {
            _harmony = h;
            var wake = new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(Wake), BindingFlags.NonPublic | BindingFlags.Static));
            var t = typeof(AudioSource);
            foreach (var m in new[]
            {
                AccessTools.Method(t, "Play", Type.EmptyTypes),
                AccessTools.Method(t, "PlayDelayed", new[] { typeof(float) }),
                AccessTools.Method(t, "PlayScheduled", new[] { typeof(double) }),
                AccessTools.Method(t, "PlayOneShot", new[] { typeof(AudioClip) }),
                AccessTools.Method(t, "PlayOneShot", new[] { typeof(AudioClip), typeof(float) }),
                AccessTools.Method(t, "UnPause"),
            })
                if (m != null) h.Patch(m, prefix: wake);
            h.Patch(AccessTools.Method(t, "Pause"), prefix: new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(OnPause), BindingFlags.NonPublic | BindingFlags.Static)));
            h.Patch(AccessTools.PropertySetter(t, "clip"), prefix: wake);
            h.Patch(AccessTools.PropertySetter(t, "playOnAwake"), postfix: new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(OnPlayOnAwake), BindingFlags.NonPublic | BindingFlags.Static)));
            h.Patch(AccessTools.Method(typeof(VideoPlayer), "Play"), prefix: new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(OnVideoPlay), BindingFlags.NonPublic | BindingFlags.Static)));
            h.Patch(AccessTools.Method(typeof(VideoPlayer), "SetTargetAudioSource"), prefix: new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(OnVideoTarget), BindingFlags.NonPublic | BindingFlags.Static)));
            // Some game code plays a sound only "if (source.isActiveAndEnabled)": a parked source must still say yes.
            h.Patch(AccessTools.PropertyGetter(typeof(Behaviour), "isActiveAndEnabled"), postfix: new HarmonyMethod(typeof(AudioParking).GetMethod(nameof(ActiveAndEnabled), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        public static void SetEnabled(bool on)
        {
            if (on == _on) return;
            _on = on;
            if (!on) WakeAll();
            _nextRescan = 0;
        }

        private static void Wake(AudioSource __instance)
        {
            if (__instance == null) return;
            Touched[__instance] = Time.unscaledTime;
            if (Parked.Remove(__instance)) __instance.enabled = true;
        }

        private static void OnPause(AudioSource __instance)
        {
            if (__instance == null) return;
            Never.Add(__instance);
            if (Parked.Remove(__instance)) __instance.enabled = true;
        }

        private static void OnVideoTarget(AudioSource source)
        {
            if (source == null) return;
            Never.Add(source);
            if (Parked.Remove(source)) source.enabled = true;
        }

        private static void OnPlayOnAwake(AudioSource __instance, bool value)
        {
            if (__instance == null) return;
            if (value) { NoAwake.Remove(__instance); if (Parked.Remove(__instance)) __instance.enabled = true; }
            else { NoAwake.Add(__instance); SharedIds().Add(__instance.GetInstanceID()); }
        }

        private static HashSet<int> SharedIds()
        {
            if (!(AppDomain.CurrentDomain.GetData(SharedKey) is HashSet<int> ids))
                AppDomain.CurrentDomain.SetData(SharedKey, ids = new HashSet<int>());
            return ids;
        }

        // A video may send its sound to a source set up before this mod was loaded: wake the sources next to it.
        private static void OnVideoPlay(VideoPlayer __instance)
        {
            if (__instance == null || Parked.Count == 0) return;
            var root = __instance.transform.parent != null ? __instance.transform.parent : __instance.transform;
            foreach (var a in root.GetComponentsInChildren<AudioSource>(true))
            {
                Never.Add(a);
                if (Parked.Remove(a)) a.enabled = true;
            }
        }

        private static void ActiveAndEnabled(Behaviour __instance, ref bool __result)
        {
            if (__result || Parked.Count == 0) return;
            if (__instance is AudioSource a && Parked.Contains(a)) __result = a.gameObject.activeInHierarchy;
        }

        /// <summary>Checks a slice of the sources each frame.</summary>
        public static void Tick()
        {
            if (!_on) return;
            float now = Time.unscaledTime;
            if (now > _nextRescan)
            {
                _nextRescan = now + 4f;
                Rescan();
            }
            int n = Math.Min(_all.Count, 150);
            for (int i = 0; i < n; i++)
            {
                if (_cursor >= _all.Count) _cursor = 0;
                var a = _all[_cursor++];
                if (a == null || Parked.Contains(a) || Never.Contains(a)) continue;
                if (!(NoAwake.Contains(a) || a.clip == null) || !a.enabled || a.isPlaying || !a.gameObject.activeInHierarchy) continue;
                if (Touched.TryGetValue(a, out var t) && now - t < 3f) continue;
                Parked.Add(a);
                a.enabled = false;
            }
        }

        private static void Rescan()
        {
            Parked.RemoveWhere(a => a == null || a.enabled); // the game switched it back on itself
            Never.RemoveWhere(a => a == null);
            NoAwake.RemoveWhere(a => a == null);
            var dead = new List<AudioSource>();
            foreach (var k in Touched.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) Touched.Remove(k);

            _all = new List<AudioSource>(UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None));
            var shared = SharedIds();
            foreach (var a in _all) if (a != null && shared.Contains(a.GetInstanceID())) NoAwake.Add(a);
            foreach (var a in _all)
                if (a != null && !Never.Contains(a) && HasAudioFilter(a.gameObject)) Never.Add(a);
        }

        // Sources that feed voice chat / TTS through OnAudioFilterRead keep running.
        private static bool HasAudioFilter(GameObject go)
        {
            foreach (var b in go.GetComponents<MonoBehaviour>())
            {
                if (b == null) continue;
                var t = b.GetType();
                if (!FilterTypes.TryGetValue(t, out var has))
                    FilterTypes[t] = has = t.GetMethod("OnAudioFilterRead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
                if (has) return true;
            }
            return false;
        }

        public static void WakeAll()
        {
            foreach (var a in Parked) if (a != null) a.enabled = true;
            Parked.Clear();
        }
    }
}
