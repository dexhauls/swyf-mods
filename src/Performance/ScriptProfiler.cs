using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Wraps every Update / LateUpdate / FixedUpdate of the game's (and other mods') scripts in a stopwatch for a
    /// few seconds, then unpatches. Shows which scripts eat the frame.
    /// </summary>
    internal static class ScriptProfiler
    {
        private static readonly string[] SkipPrefixes =
        {
            "Unity", "UnityEngine", "System", "mscorlib", "Mono.", "BepInEx", "0Harmony", "HarmonyX", "MonoMod",
            "netstandard", "Newtonsoft", "Performance", "Microsoft", "SwyfModManager", "Steamworks", "com.rlabrecque",
        };

        private static Harmony _harmony;
        private static readonly Dictionary<MethodBase, int> Index = new Dictionary<MethodBase, int>();
        private static readonly List<MethodBase> Methods = new List<MethodBase>();
        private static double[] _ms;
        private static long[] _calls;
        private static int _startFrame;
        private static int _patched, _failed;

        public static bool Running => _harmony != null;

        public static void Start()
        {
            if (_harmony != null) return;
            _harmony = new Harmony(Plugin.PluginGuid + ".profiler");
            Index.Clear();
            Methods.Clear();
            _patched = _failed = 0;
            var prefix = new HarmonyMethod(typeof(ScriptProfiler).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(ScriptProfiler).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var m in Candidates())
            {
                Index[m] = Methods.Count;
                Methods.Add(m);
            }
            _ms = new double[Methods.Count];
            _calls = new long[Methods.Count];
            foreach (var m in Methods)
            {
                try { _harmony.Patch(m, prefix, postfix); _patched++; }
                catch { _failed++; }
            }
            _startFrame = Time.frameCount;
        }

        private static IEnumerable<MethodBase> Candidates()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (SkipPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsGenericTypeDefinition || t.IsInterface) continue;
                    bool behaviour;
                    try { behaviour = typeof(MonoBehaviour).IsAssignableFrom(t); } catch { continue; }
                    if (!behaviour) continue;
                    foreach (var n in new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI" })
                    {
                        MethodInfo m;
                        try { m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null); }
                        catch { continue; }
                        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        yield return m;
                    }
                }
            }
        }

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            long dt = Stopwatch.GetTimestamp() - __state;
            if (__originalMethod != null && Index.TryGetValue(__originalMethod, out int i))
            {
                _ms[i] += dt * 1000.0 / Stopwatch.Frequency;
                _calls[i]++;
            }
        }

        public static void Stop()
        {
            if (_harmony == null) return;
            try { _harmony.UnpatchSelf(); } catch (Exception e) { Plugin.Log.LogWarning("Profiler unpatch: " + e.Message); }
            _harmony = null;
        }

        public static string Report()
        {
            int frames = Math.Max(1, Time.frameCount - _startFrame);
            var sb = new StringBuilder();
            double total = _ms.Sum();
            sb.AppendLine($"Scripts over {frames} frames: {_patched} methods patched ({_failed} failed), {total / frames:F3} ms/frame in total (includes profiler overhead ~0.3 us per call)");
            var order = Enumerable.Range(0, Methods.Count).OrderByDescending(i => _ms[i]).Take(45);
            foreach (int i in order)
            {
                if (_calls[i] == 0) break;
                var m = Methods[i];
                sb.AppendLine($"  {_ms[i] / frames,7:F3} ms  {(double)_calls[i] / frames,6:F1}x  {m.DeclaringType.Assembly.GetName().Name}: {m.DeclaringType.FullName}.{m.Name}");
            }
            return sb.ToString();
        }
    }
}
