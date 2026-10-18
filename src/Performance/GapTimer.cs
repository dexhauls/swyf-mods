using System.Diagnostics;
using UnityEngine.LowLevel;

namespace Performance
{
    /// <summary>
    /// Live cost of the window-message pump (where Windows hands the game mouse and keyboard input): the time
    /// between the end of TimeUpdate and the start of Initialization. Two markers, nearly free.
    /// </summary>
    internal static class GapTimer
    {
        private static bool _installed;
        private static long _start;
        private static double _accum;
        private static int _frames;
        public static double LastMs, FrameMs;

        public static void Set(bool on)
        {
            if (on == _installed || LoopTimer.Running) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null || loop.subSystemList.Length < 2) return;
            var a = loop.subSystemList[0];
            var b = loop.subSystemList[1];
            if (on)
            {
                a.subSystemList = Append(a.subSystemList, new PlayerLoopSystem { type = typeof(GapTimer), updateDelegate = Begin }, true);
                b.subSystemList = Append(b.subSystemList, new PlayerLoopSystem { type = typeof(GapTimer), updateDelegate = End }, false);
            }
            else
            {
                a.subSystemList = Remove(a.subSystemList);
                b.subSystemList = Remove(b.subSystemList);
            }
            loop.subSystemList[0] = a;
            loop.subSystemList[1] = b;
            PlayerLoop.SetPlayerLoop(loop);
            _installed = on;
        }

        private static PlayerLoopSystem[] Append(PlayerLoopSystem[] list, PlayerLoopSystem s, bool atEnd)
        {
            list = list ?? new PlayerLoopSystem[0];
            var r = new PlayerLoopSystem[list.Length + 1];
            if (atEnd) { list.CopyTo(r, 0); r[list.Length] = s; }
            else { r[0] = s; list.CopyTo(r, 1); }
            return r;
        }

        private static PlayerLoopSystem[] Remove(PlayerLoopSystem[] list)
        {
            var r = new System.Collections.Generic.List<PlayerLoopSystem>();
            foreach (var s in list ?? new PlayerLoopSystem[0]) if (s.type != typeof(GapTimer)) r.Add(s);
            return r.ToArray();
        }

        private static void Begin() => _start = Stopwatch.GetTimestamp();

        private static void End()
        {
            if (_start == 0) return;
            FrameMs = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            _accum += FrameMs;
            if (++_frames >= 20) { LastMs = _accum / _frames; _accum = 0; _frames = 0; }
        }
    }
}
