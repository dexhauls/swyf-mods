using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>
    /// Thousands of small props (mugs, pens, keyboards…) cast shadows into 4 shadow cascades, which is most of the
    /// game's ~5000 batches. A prop now casts a shadow only while the camera is closer than
    /// max(minDistance, size × factor): a 30 cm mug up to 8 m, a 2 m desk up to 40 m. Far away the shadow is a few pixels.
    /// </summary>
    internal static class ShadowCulling
    {
        private struct Entry { public Renderer R; public float Reach2; }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly HashSet<Renderer> Known = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> Off = new HashSet<Renderer>();
        private static float _factor, _min;
        private static int _cursor;
        private static float _nextRescan;

        public static int CulledCount => Off.Count;
        public static bool IsCulled(Renderer r) => Off.Contains(r);

        public static void Configure(float factor, float min)
        {
            if (factor == _factor && min == _min) return;
            RestoreAll();
            _factor = factor;
            _min = min;
            Entries.Clear();
            Known.Clear();
            _nextRescan = 0;
        }

        public static void Tick(Camera cam)
        {
            if (_factor <= 0) return;
            float now = Time.unscaledTime;
            if (now > _nextRescan)
            {
                _nextRescan = now + 5f;
                Rescan();
            }
            if (cam == null || Entries.Count == 0) return;
            Vector3 eye = cam.transform.position;
            // Whole list in ~8 frames.
            int n = Math.Min(Entries.Count, Entries.Count / 8 + 64);
            for (int i = 0; i < n; i++)
            {
                if (_cursor >= Entries.Count) _cursor = 0;
                var e = Entries[_cursor];
                var r = e.R;
                if (r == null) { Entries.RemoveAt(_cursor); continue; }
                _cursor++;
                bool far = r.bounds.SqrDistance(eye) > e.Reach2;
                bool isOff = Off.Contains(r);
                if (far && !isOff)
                {
                    if (r.shadowCastingMode != ShadowCastingMode.On) continue; // the game changed it, leave it alone
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    Off.Add(r);
                }
                else if (!far && isOff)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                    Off.Remove(r);
                }
            }
        }

        private static void Rescan()
        {
            Off.RemoveWhere(r => r == null);
            Known.RemoveWhere(r => r == null);
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (Known.Contains(r)) continue;
                if (r.shadowCastingMode != ShadowCastingMode.On) continue;
                // Characters and particles keep their shadows; only static-looking meshes are culled.
                if (!(r is MeshRenderer)) continue;
                Known.Add(r);
                float size = r.bounds.size.magnitude;
                float reach = Mathf.Max(_min, size * _factor);
                if (reach > 200f) continue; // walls, floors: always
                Entries.Add(new Entry { R = r, Reach2 = reach * reach });
            }
        }

        public static void RestoreAll()
        {
            foreach (var r in Off)
                if (r != null && r.shadowCastingMode == ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.On;
            Off.Clear();
        }
    }
}
