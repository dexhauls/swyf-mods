using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>
    /// Frame time, CPU/GPU split (FrameTimingManager), draw calls (release-build profiler counters)
    /// and CPU time each camera takes to render.
    /// </summary>
    internal static class FrameStats
    {
        public const int Window = 240;
        private static readonly float[] Ring = new float[Window];
        private static int _count, _head;

        private static readonly FrameTiming[] Timing = new FrameTiming[1];
        public static double GpuMs, MainMs, RenderMs, PresentWaitMs;
        public static bool TimingAvailable;

        private static ProfilerRecorder _draw, _batches, _setPass, _tris, _shadowCasters, _gcAlloc;
        public static long DrawCalls => _draw.Valid ? _draw.LastValue : -1;
        public static long Batches => _batches.Valid ? _batches.LastValue : -1;
        public static long SetPass => _setPass.Valid ? _setPass.LastValue : -1;
        public static long Triangles => _tris.Valid ? _tris.LastValue : -1;
        public static long ShadowCasters => _shadowCasters.Valid ? _shadowCasters.LastValue : -1;
        public static long GcAlloc => _gcAlloc.Valid ? _gcAlloc.LastValue : -1;

        // Per-camera CPU (main thread) render time, summed since the last Take.
        private static readonly Dictionary<Camera, double> CamMs = new Dictionary<Camera, double>();
        private static readonly Dictionary<Camera, int> CamCount = new Dictionary<Camera, int>();
        private static long _camStart;
        private static Camera _camCurrent;
        private static bool _hooked;

        public static void Init()
        {
            _draw = Recorder("Render", "Draw Calls Count");
            _batches = Recorder("Render", "Batches Count");
            _setPass = Recorder("Render", "SetPass Calls Count");
            _tris = Recorder("Render", "Triangles Count");
            _shadowCasters = Recorder("Render", "Shadow Casters Count");
            _gcAlloc = Recorder("Memory", "GC Allocated In Frame");
        }

        private unsafe delegate ProfilerRecorderHandle GetByName(ProfilerCategory category, byte* name, int nameLen);

        // ProfilerRecorder.StartNew is stripped from the game; look the counter up through the internal API.
        private static unsafe ProfilerRecorder Recorder(string category, string stat)
        {
            try
            {
                // Counters are found by name; category 0 (Render) is only a hint. Other ids could be unregistered.
                var mi = typeof(ProfilerRecorderHandle).GetMethod("GetByName__Unmanaged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var get = (GetByName)Delegate.CreateDelegate(typeof(GetByName), mi);
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(stat);
                ushort id = 0;
                ProfilerCategory cat = *(ProfilerCategory*)&id;
                ProfilerRecorderHandle handle;
                fixed (byte* p = utf8) handle = get(cat, p, utf8.Length);
                if (!handle.Valid) return default;
                var r = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                r.Start();
                return r;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Profiler counter '{stat}': {e.Message}");
                return default;
            }
        }

        public static void Shutdown()
        {
            _draw.Dispose(); _batches.Dispose(); _setPass.Dispose(); _tris.Dispose(); _shadowCasters.Dispose(); _gcAlloc.Dispose();
            HookCameras(false);
        }

        public static void HookCameras(bool on)
        {
            if (on == _hooked) return;
            _hooked = on;
            if (on)
            {
                RenderPipelineManager.beginCameraRendering += BeginCam;
                RenderPipelineManager.endCameraRendering += EndCam;
            }
            else
            {
                RenderPipelineManager.beginCameraRendering -= BeginCam;
                RenderPipelineManager.endCameraRendering -= EndCam;
            }
        }

        private static void BeginCam(ScriptableRenderContext ctx, Camera cam)
        {
            _camCurrent = cam;
            _camStart = Stopwatch.GetTimestamp();
        }

        private static void EndCam(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _camCurrent || cam == null) return;
            double ms = (Stopwatch.GetTimestamp() - _camStart) * 1000.0 / Stopwatch.Frequency;
            CamMs.TryGetValue(cam, out var t);
            CamMs[cam] = t + ms;
            CamCount.TryGetValue(cam, out var n);
            CamCount[cam] = n + 1;
        }

        public static List<string> TakeCameraReport(int frames)
        {
            var list = new List<KeyValuePair<Camera, double>>(CamMs);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var lines = new List<string>();
            foreach (var kv in list)
            {
                string name = kv.Key != null ? SceneReport.Path(kv.Key.transform) : "(destroyed)";
                lines.Add($"  {kv.Value / Math.Max(1, frames),7:F3} ms  {(double)CamCount[kv.Key] / Math.Max(1, frames):F2}x/frame  {name}");
            }
            CamMs.Clear();
            CamCount.Clear();
            return lines;
        }

        /// <summary>Call once per frame.</summary>
        public static void Tick()
        {
            Ring[_head] = Time.unscaledDeltaTime;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, Timing) > 0)
            {
                TimingAvailable = true;
                GpuMs = Timing[0].gpuFrameTime;
                MainMs = Timing[0].cpuMainThreadFrameTime;
                RenderMs = Timing[0].cpuRenderThreadFrameTime;
                PresentWaitMs = Timing[0].cpuMainThreadPresentWaitTime;
            }
        }

        /// <summary>Average frame time (ms) over the last <paramref name="frames"/> frames.</summary>
        public static double AverageMs(int frames)
        {
            frames = Math.Min(frames, _count);
            if (frames == 0) return 0;
            double sum = 0;
            for (int i = 1; i <= frames; i++) sum += Ring[(_head - i + Window) % Window];
            return sum * 1000.0 / frames;
        }

        /// <summary>Frame time (ms) of the <paramref name="ago"/>-th last frame, 1 = the last one; 0 if not recorded.</summary>
        public static double RecentMs(int ago) => ago < 1 || ago > _count ? 0 : Ring[(_head - ago + Window) % Window] * 1000.0;

        public static double WorstMs(int frames)
        {
            frames = Math.Min(frames, _count);
            double worst = 0;
            for (int i = 1; i <= frames; i++) worst = Math.Max(worst, Ring[(_head - i + Window) % Window]);
            return worst * 1000.0;
        }
    }
}
