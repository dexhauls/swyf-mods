using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Performance
{
    /// <summary>
    /// Why does the frame rate drop while turning the camera? Two measurements:
    /// "turnwatch": the player turns with the mouse; frames are split into still / turning, with input events per frame.
    /// "sweep": the mod itself turns the view (no mouse): cost of each viewing direction and of a smooth turn.
    /// </summary>
    internal static class TurnProbe
    {
        private static Quaternion _lastRot;
        private static bool _haveRot;
        private static int _inputEvents, _mouseEvents;
        private static bool _hooked;

        // Win32 message counter on the game's main thread (where Unity pumps window messages).
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static HookProc _proc;
        private static IntPtr _hook;
        private static int _wmInput, _wmMouse, _wmAll;

        private static IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam.ToInt64() == 1) // PM_REMOVE: the message is really being handled
            {
                // MSG: hwnd (8), message (4) on x64.
                int msg = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, IntPtr.Size);
                _wmAll++;
                if (msg == 0x00FF) _wmInput++;
                else if (msg >= 0x0200 && msg <= 0x020E) _wmMouse++;
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private static void HookMessages(bool on)
        {
            if (on && _hook == IntPtr.Zero)
            {
                _proc = OnMessage;
                _hook = SetWindowsHookEx(3 /* WH_GETMESSAGE */, _proc, IntPtr.Zero, GetCurrentThreadId());
                if (_hook == IntPtr.Zero) Plugin.Log.LogWarning("Message hook failed.");
            }
            else if (!on && _hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        // ---------- per-frame classification (called very late in the frame) ----------

        public static void HookInput(bool on)
        {
            if (on == _hooked) return;
            _hooked = on;
            HookMessages(on);
            try
            {
                if (on) InputSystem.onEvent += OnEvent;
                else InputSystem.onEvent -= OnEvent;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Input event hook: " + e.Message); }
        }

        private static void OnEvent(InputEventPtr e, InputDevice device)
        {
            _inputEvents++;
            if (device is Mouse) _mouseEvents++;
        }

        /// <summary>Mouse window messages (WM_INPUT + WM_MOUSE*) since the last call.</summary>
        public static int TakeMouseMessages()
        {
            int n = _wmInput + _wmMouse;
            _inputEvents = _mouseEvents = _wmInput = _wmMouse = _wmAll = 0;
            return n;
        }

        /// <summary>Tags the frame for LoopTimer: bucket 1 = the camera turned this frame.</summary>
        public static void TagFrame(Camera cam)
        {
            float deg = 0;
            if (cam != null)
            {
                var rot = cam.transform.rotation;
                if (_haveRot) deg = Quaternion.Angle(rot, _lastRot);
                _lastRot = rot;
                _haveRot = true;
            }
            LoopTimer.FrameBucket = deg > 0.05f ? 1 : 0;
            StackSampler.Bucket = LoopTimer.FrameBucket;
            LoopTimer.FrameExtras[0] = Math.Max(0, FrameStats.Batches);
            LoopTimer.FrameExtras[1] = _mouseEvents;
            LoopTimer.FrameExtras[2] = _wmInput;
            LoopTimer.FrameExtras[3] = deg;
            LoopTimer.FrameExtras[4] = _wmMouse;
            LoopTimer.FrameExtras[5] = _wmAll;
            _inputEvents = _mouseEvents = _wmInput = _wmMouse = _wmAll = 0;
        }

        public static readonly string[] BucketNames = { "still", "turning" };
        public static readonly string[] ExtraNames = { "batches (prev frame)", "InputSystem mouse ev", "WM_INPUT/frame", "degrees/frame", "WM_MOUSE*/frame", "all messages/frame" };

        // ---------- synthetic sweep ----------

        private sealed class Sweep
        {
            public Camera Cam;
            public Quaternion Start;
            public float Yaw0, Pitch;
            public float T0;
            public readonly List<(float yaw, List<float> ms, double batches)> Dirs = new List<(float, List<float>, double)>();
            public readonly List<float> Turning = new List<float>();
            public double TurnBatches;
            public int Phase; // 0 dwell, 1 turning, 2 done
        }

        private static Sweep _sweep;
        private const int Directions = 12;
        private const float Dwell = 1.0f, Settle = 0.25f, TurnSeconds = 8f;
        public static float TurnSpeed = 270f;

        public static bool Sweeping => _sweep != null;
        private static int _vsync, _target;

        private static void RestoreFrameSettings()
        {
            QualitySettings.vSyncCount = _vsync;
            Application.targetFrameRate = _target;
            FrameLimiter.Suspended = false;
        }

        public static void StartSweep(Camera cam)
        {
            if (cam == null) return;
            var e = cam.transform.rotation.eulerAngles;
            _sweep = new Sweep { Cam = cam, Start = cam.transform.rotation, Yaw0 = e.y, Pitch = e.x > 180 ? e.x - 360 : e.x, T0 = Time.realtimeSinceStartup };
            // Uncapped, like the benchmark: with VSync frame times snap to refresh intervals.
            FrameLimiter.Release();
            FrameLimiter.Suspended = true;
            _vsync = QualitySettings.vSyncCount;
            _target = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }

        /// <summary>Very late in the frame: overrides the camera rotation for this frame only.</summary>
        public static string SweepTick()
        {
            var s = _sweep;
            if (s == null) return null;
            if (s.Cam == null) { _sweep = null; return "Sweep aborted: camera gone."; }
            float t = Time.realtimeSinceStartup - s.T0;
            float ms = Time.unscaledDeltaTime * 1000f;
            float pitch = Mathf.Clamp(s.Pitch, -10, 10);
            if (t < Directions * Dwell)
            {
                int d = (int)(t / Dwell);
                float yaw = s.Yaw0 + d * 360f / Directions;
                while (s.Dirs.Count <= d) s.Dirs.Add((yaw, new List<float>(), 0));
                if (t - d * Dwell > Settle)
                {
                    s.Dirs[d].ms.Add(ms);
                    var x = s.Dirs[d];
                    s.Dirs[d] = (x.yaw, x.ms, x.batches + Math.Max(0, FrameStats.Batches));
                }
                s.Cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
                return null;
            }
            float tt = t - Directions * Dwell;
            if (tt < TurnSeconds)
            {
                if (tt > 0.2f) { s.Turning.Add(ms); s.TurnBatches += Math.Max(0, FrameStats.Batches); }
                s.Cam.transform.rotation = Quaternion.Euler(pitch, s.Yaw0 + tt * TurnSpeed, 0);
                return null;
            }
            s.Cam.transform.rotation = s.Start;
            _sweep = null;
            RestoreFrameSettings();
            return Report(s);
        }

        private static string Report(Sweep s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Sweep {DateTime.Now:HH:mm:ss} at {s.Cam.transform.position} on {SystemInfo.graphicsDeviceType} (view turned by the mod, no mouse, uncapped; preset {Plugin.Instance?.CurrentPreset}):");
            var all = new List<float>();
            foreach (var (yaw, ms, batches) in s.Dirs)
            {
                if (ms.Count == 0) continue;
                all.AddRange(ms);
                double avg = 0; foreach (var m in ms) avg += m; avg /= ms.Count;
                sb.AppendLine($"  yaw {Mathf.Repeat(yaw, 360),5:F0}°  {avg,6:F2} ms ({1000 / avg,4:F0} fps)  batches {batches / ms.Count,6:F0}");
            }
            double still = 0; foreach (var m in all) still += m; still /= Math.Max(1, all.Count);
            double turn = 0, worst = 0; foreach (var m in s.Turning) { turn += m; worst = Math.Max(worst, m); } turn /= Math.Max(1, s.Turning.Count);
            sb.AppendLine($"  all directions, standing: {still:F2} ms ({1000 / Math.Max(0.01, still):F0} fps)");
            sb.AppendLine($"  smooth turn {TurnSpeed}°/s: {turn:F2} ms ({1000 / Math.Max(0.01, turn):F0} fps), worst {worst:F2} ms, batches {s.TurnBatches / Math.Max(1, s.Turning.Count):F0}");
            return sb.ToString();
        }

        public static void AbortSweep()
        {
            if (_sweep?.Cam != null) _sweep.Cam.transform.rotation = _sweep.Start;
            if (_sweep != null) RestoreFrameSettings();
            _sweep = null;
        }
    }
}
