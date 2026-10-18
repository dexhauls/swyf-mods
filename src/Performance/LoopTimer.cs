using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine.LowLevel;

namespace Performance
{
    /// <summary>
    /// Times every step of Unity's player loop (scripts, physics, UI, rendering, waiting for the GPU…)
    /// by putting a tiny marker between the steps. Stands in for the profiler, which a release build doesn't have.
    /// Frames are sorted into buckets (e.g. "camera still" / "camera turning") so the two can be compared.
    /// </summary>
    internal static class LoopTimer
    {
        public const int Buckets = 2;

        /// <summary>Bucket of the frame that is running now; set late in the frame by the caller.</summary>
        public static int FrameBucket;
        /// <summary>Extra per-frame numbers (batches, input events…) recorded with the frame's bucket.</summary>
        public static double[] FrameExtras = new double[ExtraCount];
        public const int ExtraCount = 6;

        private static PlayerLoopSystem _original;
        private static bool _installed;
        private static readonly List<string> Names = new List<string>();
        private static double[] _cur;
        private static double[][] _total;
        private static double[][] _extras;
        private static int[] _frames;
        private static List<double>[] _frameMs;
        private static double _max;
        private static long _last;
        private static int _lastIndex = -1;
        private static long _frameStart;

        public static bool Running => _installed;

        // ---------- hitches: frames slower than HitchMs, with what took the time ----------

        /// <summary>Frames longer than this (ms) are recorded with their per-step breakdown; 0 = off.</summary>
        public static double HitchMs;
        private sealed class Hitch { public float Time; public double Ms; public bool Gc, FullGc; public (int step, double ms)[] Top; }
        private static readonly List<Hitch> Hitches = new List<Hitch>();
        private static double[] _hitchSteps;
        private static int _gc0, _gc2, _allFrames, _hitchCount, _gcHitches;

        private static void RecordHitch(double ms, bool gc, bool fullGc)
        {
            _hitchCount++;
            if (gc) _gcHitches++;
            if (_hitchSteps == null || _hitchSteps.Length != _cur.Length) _hitchSteps = new double[_cur.Length];
            for (int i = 0; i < _cur.Length; i++) _hitchSteps[i] += _cur[i];
            if (Hitches.Count >= 300) return;
            var top = new List<(int, double)>();
            for (int i = 0; i < _cur.Length; i++) if (_cur[i] > 0.5) top.Add((i, _cur[i]));
            top.Sort((a, c) => c.Item2.CompareTo(a.Item2));
            if (top.Count > 4) top.RemoveRange(4, top.Count - 4);
            Hitches.Add(new Hitch { Time = UnityEngine.Time.realtimeSinceStartup, Ms = ms, Gc = gc, FullGc = fullGc, Top = top.ToArray() });
        }

        public static string HitchReport()
        {
            var sb = new StringBuilder();
            double secs = Hitches.Count > 0 ? Hitches[Hitches.Count - 1].Time - Hitches[0].Time : 0;
            sb.AppendLine($"Hitches over {HitchMs:F0} ms: {_hitchCount} of {_allFrames} frames ({100.0 * _hitchCount / Math.Max(1, _allFrames):F2} %), {_gcHitches} with a garbage collection");
            if (_hitchSteps != null && _hitchCount > 0)
            {
                sb.AppendLine("  where the time went in hitch frames (avg per hitch):");
                var idx = new List<int>();
                for (int i = 0; i < _hitchSteps.Length; i++) idx.Add(i);
                idx.Sort((a, c) => _hitchSteps[c].CompareTo(_hitchSteps[a]));
                foreach (int i in idx)
                {
                    double v = _hitchSteps[i] / _hitchCount;
                    if (v < 0.3) break;
                    sb.AppendLine($"    {v,7:F2} ms  {Names[i]}");
                }
            }
            var worst = new List<Hitch>(Hitches);
            worst.Sort((a, c) => c.Ms.CompareTo(a.Ms));
            sb.AppendLine("  worst frames:");
            foreach (var h in worst.GetRange(0, Math.Min(15, worst.Count)))
            {
                var parts = new List<string>();
                foreach (var (step, ms) in h.Top) parts.Add($"{Names[step]} {ms:F1}");
                sb.AppendLine($"    t={h.Time,7:F1}s {h.Ms,7:F1} ms {(h.FullGc ? "FULL-GC " : h.Gc ? "GC " : "")}| {string.Join(", ", parts)}");
            }
            return sb.ToString();
        }
        public static int FramesIn(int bucket) => _installed ? _frames[bucket] : 0;

        public static void Start()
        {
            if (_installed) return;
            _original = PlayerLoop.GetCurrentPlayerLoop();
            Names.Clear();
            var root = _original;
            var phases = new PlayerLoopSystem[root.subSystemList.Length];
            for (int p = 0; p < phases.Length; p++)
            {
                var phase = root.subSystemList[p];
                var subs = phase.subSystemList ?? new PlayerLoopSystem[0];
                var list = new List<PlayerLoopSystem>();
                foreach (var sub in subs)
                {
                    list.Add(Marker(Names.Count, p == 0 && list.Count == 0));
                    Names.Add(phase.type?.Name + "." + sub.type?.Name);
                    list.Add(sub);
                }
                list.Add(Marker(Names.Count, false));
                Names.Add(phase.type?.Name + ".(end)");
                phase.subSystemList = list.ToArray();
                phases[p] = phase;
            }
            root.subSystemList = phases;
            _cur = new double[Names.Count];
            _total = new double[Buckets][];
            _extras = new double[Buckets][];
            _frames = new int[Buckets];
            _frameMs = new List<double>[Buckets];
            for (int b = 0; b < Buckets; b++)
            {
                _total[b] = new double[Names.Count];
                _extras[b] = new double[ExtraCount];
                _frameMs[b] = new List<double>();
            }
            _max = 0;
            Hitches.Clear();
            _hitchSteps = null;
            _hitchCount = _gcHitches = _allFrames = 0;
            _gc0 = GC.CollectionCount(0); _gc2 = GC.CollectionCount(2);
            _lastIndex = -1;
            _frameStart = 0;
            FrameBucket = 0;
            PlayerLoop.SetPlayerLoop(root);
            _installed = true;
        }

        private static PlayerLoopSystem Marker(int index, bool frameStart) => new PlayerLoopSystem
        {
            type = typeof(LoopTimer),
            updateDelegate = () => Mark(index, frameStart),
        };

        private static void Mark(int index, bool frameStart)
        {
            long now = Stopwatch.GetTimestamp();
            // The time since the previous marker belongs to the step that ran between them.
            if (_lastIndex >= 0)
            {
                int step = index - 1 >= 0 && !frameStart ? index - 1 : Names.Count - 1;
                _cur[step] += (now - _last) * 1000.0 / Stopwatch.Frequency;
            }
            if (frameStart)
            {
                if (_frameStart != 0 && _lastIndex >= 0)
                {
                    int b = Math.Max(0, Math.Min(Buckets - 1, FrameBucket));
                    double ms = (now - _frameStart) * 1000.0 / Stopwatch.Frequency;
                    _frameMs[b].Add(ms);
                    if (ms > _max) _max = ms;
                    _frames[b]++;
                    int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
                    if (HitchMs > 0 && ms > HitchMs) RecordHitch(ms, gc0 != _gc0, gc2 != _gc2);
                    _gc0 = gc0; _gc2 = gc2;
                    _allFrames++;
                    var t = _total[b];
                    for (int i = 0; i < _cur.Length; i++) { t[i] += _cur[i]; _cur[i] = 0; }
                    for (int i = 0; i < ExtraCount; i++) _extras[b][i] += FrameExtras[i];
                }
                else Array.Clear(_cur, 0, _cur.Length);
                _frameStart = now;
            }
            _last = Stopwatch.GetTimestamp();
            _lastIndex = index;
        }

        public static void Stop()
        {
            if (!_installed) return;
            PlayerLoop.SetPlayerLoop(_original);
            _installed = false;
            _frameStart = 0;
        }

        private static string Stats(int b)
        {
            var list = _frameMs[b];
            list.Sort();
            if (list.Count == 0) return "no frames";
            double sum = 0;
            foreach (var t in _total[b]) sum += t;
            return $"{_frames[b]} frames, avg {sum / _frames[b]:F2} ms, median {list[list.Count / 2]:F2}, p95 {list[(int)(list.Count * 0.95)]:F2}, max {list[list.Count - 1]:F2}";
        }

        public static string Report(string[] bucketNames = null, string[] extraNames = null)
        {
            var sb = new StringBuilder();
            bool split = _frames[1] > 0;
            if (!split)
            {
                int frames = Math.Max(1, _frames[0]);
                sb.AppendLine($"Player loop: {Stats(0)}, max {_max:F2}");
                var order = new List<int>();
                for (int i = 0; i < Names.Count; i++) order.Add(i);
                order.Sort((a, c) => _total[0][c].CompareTo(_total[0][a]));
                foreach (int i in order)
                {
                    double ms = _total[0][i] / frames;
                    if (ms < 0.02) break;
                    sb.AppendLine($"  {ms,7:F3} ms  {Names[i]}");
                }
                return sb.ToString();
            }
            int f0 = Math.Max(1, _frames[0]), f1 = Math.Max(1, _frames[1]);
            string n0 = bucketNames?[0] ?? "A", n1 = bucketNames?[1] ?? "B";
            sb.AppendLine($"Player loop, {n0}: {Stats(0)}");
            sb.AppendLine($"Player loop, {n1}: {Stats(1)}");
            if (extraNames != null)
                for (int e = 0; e < ExtraCount && e < extraNames.Length; e++)
                    if (extraNames[e] != null)
                        sb.AppendLine($"  {extraNames[e],-22} {n0} {_extras[0][e] / f0,9:F1}   {n1} {_extras[1][e] / f1,9:F1}");
            sb.AppendLine($"  {"step",-48} {n0,9} {n1,9}   diff");
            var idx = new List<int>();
            for (int i = 0; i < Names.Count; i++) idx.Add(i);
            idx.Sort((a, c) => Math.Abs(_total[1][c] / f1 - _total[0][c] / f0).CompareTo(Math.Abs(_total[1][a] / f1 - _total[0][a] / f0)));
            foreach (int i in idx)
            {
                double a = _total[0][i] / f0, c = _total[1][i] / f1;
                if (Math.Max(a, c) < 0.03) continue;
                sb.AppendLine($"  {Names[i],-48} {a,9:F3} {c,9:F3} {c - a,+8:+0.000;-0.000}");
                if (sb.Length > 6000) break;
            }
            return sb.ToString();
        }
    }
}
