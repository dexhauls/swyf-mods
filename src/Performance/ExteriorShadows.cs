using System.Collections.Generic;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Three street lights outside the office ("Office Map/Exterior/Point Light…") have a 52.5 m range and shadows:
    /// a point light draws its shadows six times (cube map), and 52 m covers the whole floor, so they redrew every
    /// prop inside ~6× each — about 4000 extra batches, 1.5 ms. From inside the office they change nothing on screen
    /// (pixel comparison), so their shadows are off while the camera is inside a floor (always in Max performance).
    /// </summary>
    internal static class ExteriorShadows
    {
        private static readonly Dictionary<Light, LightShadows> Saved = new Dictionary<Light, LightShadows>();
        private static int _mode; // 0 off, 1 only when inside, 2 always
        private static float _next;
        private static OfficeFloorStreaming _floors;

        public static int Count => Saved.Count;

        public static void SetMode(int mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            _next = 0;
            if (mode == 0) Restore();
        }

        public static void Tick(Camera cam)
        {
            if (_mode == 0 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            bool off = _mode == 2 || (cam != null && Inside(cam.transform.position));
            if (!off) { Restore(); return; }
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Point || l.range < 30f || l.shadows == LightShadows.None || Saved.ContainsKey(l)) continue;
                var p = l.transform.parent;
                if (p == null || p.name != "Exterior") continue;
                Saved[l] = l.shadows;
                l.shadows = LightShadows.None;
            }
        }

        private static bool Inside(Vector3 pos)
        {
            if (_floors == null) _floors = Object.FindAnyObjectByType<OfficeFloorStreaming>();
            if (_floors == null) return false;
            foreach (var f in _floors.floors)
                if (f != null && f.interior.size.sqrMagnitude > 0 && f.interior.Contains(pos)) return true;
            return false;
        }

        public static void Restore()
        {
            foreach (var kv in Saved) if (kv.Key != null && kv.Key.shadows == LightShadows.None) kv.Key.shadows = kv.Value;
            Saved.Clear();
        }
    }
}
