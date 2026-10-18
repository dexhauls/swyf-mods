using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>
    /// The game gives ~1600 office props their own baked light-probe lighting through a MaterialPropertyBlock
    /// (OfficePropProbeLighting). A property block takes the renderer out of the SRP Batcher, so every prop costs
    /// its own state change: measured 1 ms a frame. Max performance lets those props use the scene's light probes
    /// instead (a slightly different shade on small props). Other per-object values (book tints…) are kept.
    /// </summary>
    internal static class PropProbes
    {
        private static readonly int[] Keep =
        {
            Shader.PropertyToID("_BaseColor"), Shader.PropertyToID("_Color"), Shader.PropertyToID("_EmissionColor"),
            Shader.PropertyToID("_Tint"), Shader.PropertyToID("_BaseMap"), Shader.PropertyToID("_MainTex"),
        };
        private static bool _on;
        private static float _nextScan;
        public static int Swapped;

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(OfficePropProbeLighting), "ApplyBakedLighting");
            if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(PropProbes).GetMethod(nameof(SkipBake), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        private static bool SkipBake() => !_on;

        public static void SetEnabled(bool on)
        {
            if (on == _on) return;
            _on = on;
            Swapped = 0;
            if (on) { _nextScan = 0; return; }
            // Back to the game's own lighting: let every component apply its baked values again.
            var apply = AccessTools.Method(typeof(OfficePropProbeLighting), "ApplyBakedLighting");
            foreach (var c in Object.FindObjectsByType<OfficePropProbeLighting>(FindObjectsSortMode.None))
                try { apply.Invoke(c, null); } catch { }
        }

        public static void Tick()
        {
            if (!_on || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 5f; // props spawned or re-baked since
            var block = new MaterialPropertyBlock();
            foreach (var c in Object.FindObjectsByType<OfficePropProbeLighting>(FindObjectsSortMode.None))
                foreach (var r in c.renderers)
                {
                    if (r == null || r.lightProbeUsage != LightProbeUsage.CustomProvided) continue;
                    r.GetPropertyBlock(block);
                    if (block.isEmpty) continue; // only OfficePropProbeLighting makes them CustomProvided
                    var rest = new MaterialPropertyBlock();
                    bool any = false;
                    foreach (int id in Keep)
                    {
                        // Vector/texture getters are stripped from the game; tints are colours.
                        if (block.HasColor(id)) { rest.SetColor(id, block.GetColor(id)); any = true; }
                    }
                    r.SetPropertyBlock(any ? rest : null);
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    Swapped++;
                }
        }
    }
}
