using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Blind A/B of the WM_SETCURSOR filter while the player turns with the mouse: the filter flips every 3 s inside
    /// one recording, so how fast the player turns hits both sides equally. Cost is compared per mouse message and
    /// per turning frame, block by block, with a spread so noise can be told from a real difference.
    /// </summary>
    internal static class CursorAB
    {
        private sealed class Block { public bool On; public int Frames; public double Dt, Gap, Msgs; }

        private const float BlockSeconds = 3f, Settle = 0.3f;
        private static readonly List<Block> Blocks = new List<Block>();
        private static Block _cur;
        private static float _until, _blockStart;
        private static int _target;
        private static Quaternion _lastRot;
        private static bool _haveRot;

        public static bool Running { get; private set; }

        public static void Start(float seconds, int targetFrames)
        {
            Blocks.Clear();
            _until = Time.realtimeSinceStartup + seconds;
            _target = targetFrames;
            _haveRot = false;
            CursorFilter.SetEnabled(true);
            TurnProbe.HookInput(true);
            GapTimer.Set(true);
            Running = true;
            NewBlock(true);
        }

        private static void NewBlock(bool on)
        {
            CursorFilter.Paused = !on;
            _cur = new Block { On = on };
            Blocks.Add(_cur);
            _blockStart = Time.realtimeSinceStartup;
        }

        /// <summary>Very late in the frame. Returns the report when done.</summary>
        public static string Tick(Camera cam)
        {
            if (!Running) return null;
            float deg = 0;
            if (cam != null)
            {
                var rot = cam.transform.rotation;
                if (_haveRot) deg = Quaternion.Angle(rot, _lastRot);
                _lastRot = rot;
                _haveRot = true;
            }
            int msgs = TurnProbe.TakeMouseMessages();
            float now = Time.realtimeSinceStartup;
            if (deg > 0.05f && now - _blockStart > Settle)
            {
                _cur.Frames++;
                _cur.Dt += Time.unscaledDeltaTime * 1000.0;
                _cur.Gap += GapTimer.FrameMs;
                _cur.Msgs += msgs;
            }
            if (now - _blockStart > BlockSeconds) NewBlock(!_cur.On);

            int on = 0, off = 0;
            foreach (var b in Blocks) { if (b.On) on += b.Frames; else off += b.Frames; }
            if (now > _until || (_target > 0 && on >= _target && off >= _target)) return Finish();
            return null;
        }

        private static string Finish()
        {
            Running = false;
            CursorFilter.Paused = false;
            TurnProbe.HookInput(false);
            var sb = new StringBuilder();
            sb.AppendLine($"=== Cursor filter A/B {DateTime.Now:HH:mm:ss} (flips every {BlockSeconds} s, turning frames only):");
            foreach (bool state in new[] { false, true })
            {
                int frames = 0; double dt = 0, gap = 0, msgs = 0;
                var perMsg = new List<double>();
                foreach (var b in Blocks)
                {
                    if (b.On != state || b.Frames < 20) continue;
                    frames += b.Frames; dt += b.Dt; gap += b.Gap; msgs += b.Msgs;
                    if (b.Msgs > 0) perMsg.Add(b.Gap / b.Msgs);
                }
                double mean = 0; foreach (var x in perMsg) mean += x; mean /= Math.Max(1, perMsg.Count);
                double sd = 0; foreach (var x in perMsg) sd += (x - mean) * (x - mean); sd = Math.Sqrt(sd / Math.Max(1, perMsg.Count - 1));
                double se = sd / Math.Sqrt(Math.Max(1, perMsg.Count));
                sb.AppendLine($"  filter {(state ? "ON " : "OFF")}: {frames} frames in {perMsg.Count} blocks, frame {dt / Math.Max(1, frames):F2} ms, " +
                              $"Windows/mouse {gap / Math.Max(1, frames):F2} ms/frame, {msgs / Math.Max(1, frames):F1} msgs/frame, " +
                              $"{gap / Math.Max(1, msgs):F3} ms per message (block mean {mean:F3} ± {se:F3})");
            }
            return sb.ToString();
        }

        public static void Abort()
        {
            if (!Running) return;
            Running = false;
            CursorFilter.Paused = false;
            TurnProbe.HookInput(false);
        }
    }
}
