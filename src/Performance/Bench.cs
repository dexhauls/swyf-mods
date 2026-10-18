using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// A/B benchmark: measures the frame with each experiment on, every time next to a fresh baseline,
    /// so drift (a player walking by, a call starting) cancels out. VSync and the frame cap are off while it runs.
    /// </summary>
    internal sealed class Bench
    {
        public sealed class Experiment
        {
            public string Name;
            public Action Apply, Revert;
            public Experiment(string name, Action apply, Action revert) { Name = name; Apply = apply; Revert = revert; }
        }

        private sealed class Sample
        {
            public readonly List<double> Frames = new List<double>();
            public double Gpu, Main, Render, Draws;
            public int TimingFrames;
            public double Avg => Frames.Count == 0 ? 0 : Frames.Average();
            public double Median { get { var s = Frames.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; } }
            public double P95 { get { var s = Frames.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[(int)(s.Count * 0.95)]; } }
        }

        private readonly List<Experiment> _experiments;
        private readonly float _settle, _measure;
        private readonly int _rounds;
        private readonly Action<string> _done;
        private readonly List<(int exp, bool on)> _plan = new List<(int, bool)>();
        private readonly Dictionary<(int, bool), List<Sample>> _results = new Dictionary<(int, bool), List<Sample>>();
        private int _step = -1;
        private float _stepStart;
        private Sample _current;
        private int _vsync, _fps;
        private readonly string _title;

        public bool Finished { get; private set; }

        public Bench(string title, List<Experiment> experiments, int rounds, float settle, float measure, Action<string> done)
        {
            _title = title;
            _experiments = experiments;
            _rounds = rounds;
            _settle = settle;
            _measure = measure;
            _done = done;
            // base, A, base, B, … per round; experiment -1 = nothing changed.
            for (int r = 0; r < rounds; r++)
                for (int e = 0; e < experiments.Count; e++)
                {
                    _plan.Add((e, false));
                    _plan.Add((e, true));
                }
            _plan.Add((_experiments.Count - 1, false));
        }

        public void Start()
        {
            _vsync = QualitySettings.vSyncCount;
            _fps = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Next();
        }

        public void Abort()
        {
            if (Finished) return;
            if (_step >= 0 && _step < _plan.Count && _plan[_step].on) SafeRevert(_plan[_step].exp);
            Restore();
            Finished = true;
        }

        private void Restore()
        {
            QualitySettings.vSyncCount = _vsync;
            Application.targetFrameRate = _fps;
        }

        private void Next()
        {
            if (_step >= 0 && _plan[_step].on) SafeRevert(_plan[_step].exp);
            _step++;
            if (_step >= _plan.Count)
            {
                Restore();
                Finished = true;
                _done(Report());
                return;
            }
            var (exp, on) = _plan[_step];
            if (on)
            {
                try { _experiments[exp].Apply(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Bench '{_experiments[exp].Name}' apply: {e.Message}"); }
            }
            _stepStart = Time.realtimeSinceStartup;
            _current = new Sample();
            if (!_results.TryGetValue((exp, on), out var list)) _results[(exp, on)] = list = new List<Sample>();
            list.Add(_current);
        }

        private void SafeRevert(int exp)
        {
            try { _experiments[exp].Revert(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Bench '{_experiments[exp].Name}' revert: {e.Message}"); }
        }

        public void Tick()
        {
            if (Finished || _step < 0) return;
            float t = Time.realtimeSinceStartup - _stepStart;
            if (t > _settle)
            {
                _current.Frames.Add(Time.unscaledDeltaTime * 1000.0);
                if (FrameStats.TimingAvailable)
                {
                    _current.Gpu += FrameStats.GpuMs;
                    _current.Main += FrameStats.MainMs;
                    _current.Render += FrameStats.RenderMs;
                    _current.TimingFrames++;
                }
                _current.Draws += Math.Max(0, FrameStats.Batches);
            }
            if (t > _settle + _measure) Next();
        }

        private string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Bench '{_title}' {DateTime.Now:HH:mm:ss}: {_rounds} round(s), {_measure:F1} s per sample, vSync/cap off. Delta vs neighbouring baselines (negative = faster).");
            // Every "off" sample is a baseline.
            var bases = _results.Where(kv => !kv.Key.Item2).SelectMany(kv => kv.Value).ToList();
            var all = new Sample();
            foreach (var b in bases) { all.Frames.AddRange(b.Frames); all.Gpu += b.Gpu; all.Main += b.Main; all.Render += b.Render; all.TimingFrames += b.TimingFrames; all.Draws += b.Draws; }
            sb.AppendLine($"  baseline: {Line(all)}");
            for (int e = 0; e < _experiments.Count; e++)
            {
                var on = Merge(_results.TryGetValue((e, true), out var l) ? l : new List<Sample>());
                // Neighbouring baselines: the "off" sample of this experiment and of the next one.
                var near = new List<Sample>();
                if (_results.TryGetValue((e, false), out var b1)) near.AddRange(b1);
                int nextE = e + 1 < _experiments.Count ? e + 1 : e;
                if (nextE != e && _results.TryGetValue((nextE, false), out var b2)) near.AddRange(b2);
                var nb = Merge(near);
                double d = on.Avg - nb.Avg;
                double pct = nb.Avg > 0 ? d / nb.Avg * 100 : 0;
                sb.AppendLine($"  {_experiments[e].Name,-34} {d,+7:+0.00;-0.00} ms ({pct,+5:+0;-0}%)  {Line(on)}   | base {nb.Avg:F2}");
            }
            return sb.ToString();
        }

        private static Sample Merge(List<Sample> list)
        {
            var m = new Sample();
            foreach (var s in list) { m.Frames.AddRange(s.Frames); m.Gpu += s.Gpu; m.Main += s.Main; m.Render += s.Render; m.TimingFrames += s.TimingFrames; m.Draws += s.Draws; }
            return m;
        }

        private static string Line(Sample s)
        {
            string timing = s.TimingFrames > 0 ? $" gpu {s.Gpu / s.TimingFrames:F2} main {s.Main / s.TimingFrames:F2} render {s.Render / s.TimingFrames:F2}" : "";
            return $"{s.Avg:F2} ms ({(s.Avg > 0 ? 1000 / s.Avg : 0):F0} fps) med {s.Median:F2} p95 {s.P95:F2}{timing} batches {(s.Frames.Count > 0 ? s.Draws / s.Frames.Count : 0):F0}";
        }
    }
}
