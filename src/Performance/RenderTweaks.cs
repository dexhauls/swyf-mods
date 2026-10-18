using System.Collections.Generic;
using Object = UnityEngine.Object;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Performance
{
    /// <summary>URP shadow settings, render-texture cameras and anti-aliasing. Everything is restored when turned off.</summary>
    internal static class RenderTweaks
    {
        private static UniversalRenderPipelineAsset _asset;
        private static int _cascades, _resolution;
        private static float _distance;
        private static Vector3 _split4;
        private static float _split2;
        private static bool _saved;

        // ---------- Main light shadows ----------

        public static void ApplyShadows(Profile p)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;
            // Never touch the game's stable source asset (the main menu runs on it before the graphics settings make
            // their runtime copy): the copy would inherit the change and this mod would take it for the original.
            var stable = GameAsset();
            if (stable != null && asset == stable) return;
            if (asset != _asset) { _asset = asset; _saved = false; }
            if (!_saved)
            {
                // The game's own values come from the asset its graphics settings copy (GraphicsQualityApplier.highAsset),
                // not from the running asset: after a hot reload that one already holds this mod's changes.
                var source = GameAsset() ?? asset;
                _cascades = source.shadowCascadeCount;
                _resolution = source.mainLightShadowmapResolution;
                _split4 = source.cascade4Split;
                _split2 = source.cascade2Split;
                _saved = true;
            }
            _distance = GameShadowDistance(asset);
            int cascades = p.ShadowCascades > 0 ? Mathf.Min(p.ShadowCascades, _cascades) : _cascades;
            int res = p.ShadowResolution > 0 ? Mathf.Max(p.ShadowResolution, _resolution) : _resolution;
            float dist = p.ShadowDistance > 0 ? Mathf.Min(p.ShadowDistance, _distance) : _distance;
            if (asset.shadowCascadeCount != cascades) asset.shadowCascadeCount = cascades;
            if (cascades == 2 && _cascades == 4 && p.ShadowCascades > 0)
                asset.cascade2Split = _split4.y; // keep the near cascade covering what cascades 1+2 covered
            else if (asset.cascade2Split != _split2) asset.cascade2Split = _split2;
            if (asset.mainLightShadowmapResolution != res) asset.mainLightShadowmapResolution = res;
            if (asset.shadowDistance != dist) asset.shadowDistance = dist;
        }

        private static UniversalRenderPipelineAsset GameAsset()
        {
            try
            {
                var applier = Object.FindAnyObjectByType<GraphicsQualityApplier>();
                return applier != null ? HarmonyLib.Traverse.Create(applier).Field("highAsset").GetValue<UniversalRenderPipelineAsset>() : null;
            }
            catch { return null; }
        }

        // Shadow distance belongs to the game's Shadows setting (Off 0, Low 60, High = the original asset's).
        private static float GameShadowDistance(UniversalRenderPipelineAsset running)
        {
            try
            {
                string shadows = PlayerPrefs.GetString("Graphics Shadows", "High");
                if (shadows == "Off") return 0f;
                if (shadows == "Low") return 60f;
                var g = GameAsset();
                if (g != null) return g.shadowDistance;
            }
            catch { }
            return _distance > 0 ? _distance : running.shadowDistance;
        }

        /// <summary>One-off repair for a game session where an older build changed the stable asset.</summary>
        public static string Repair(int cascades, int resolution)
        {
            var g = GameAsset();
            var a = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            foreach (var x in new[] { g, a })
                if (x != null) { x.shadowCascadeCount = cascades; x.mainLightShadowmapResolution = resolution; }
            _saved = false;
            return Debug();
        }

        public static string Debug()
        {
            var g = GameAsset();
            var a = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return $"game asset {(g == null ? "NOT FOUND" : $"'{g.name}' cascades {g.shadowCascadeCount} res {g.mainLightShadowmapResolution} dist {g.shadowDistance}")}; " +
                   $"running '{a?.name}' cascades {a?.shadowCascadeCount} res {a?.mainLightShadowmapResolution} dist {a?.shadowDistance}; saved {_saved} cascades {_cascades} res {_resolution} dist {_distance}, same asset {a == _asset}";
        }

        public static void RestoreShadows()
        {
            if (!_saved || _asset == null) return;
            _asset.shadowCascadeCount = _cascades;
            _asset.mainLightShadowmapResolution = _resolution;
            _asset.shadowDistance = _distance;
            _asset.cascade2Split = _split2;
            _asset.cascade4Split = _split4;
        }

        // ---------- Cameras that render into textures (boss webcam, CCTV, phone camera…) ----------

        private static readonly Dictionary<Camera, float> LastRender = new Dictionary<Camera, float>();
        private static readonly List<Camera> Held = new List<Camera>();
        private static readonly Dictionary<UniversalAdditionalCameraData, bool> ShadowOff = new Dictionary<UniversalAdditionalCameraData, bool>();
        private static readonly Camera[] CamBuffer = new Camera[64];

        /// <summary>Very late in the frame: skip this frame's render for cameras that rendered recently.</summary>
        public static void HoldTextureCameras(Profile p)
        {
            if (p.TextureCameraFps <= 0 && !p.TextureCameraNoShadows) return;
            int n = Camera.GetAllCameras(CamBuffer);
            float now = Time.unscaledTime;
            float interval = p.TextureCameraFps > 0 ? 1f / p.TextureCameraFps : 0f;
            for (int i = 0; i < n; i++)
            {
                var c = CamBuffer[i];
                CamBuffer[i] = null;
                if (c == null || c.targetTexture == null || c == Camera.main) continue;
                if (p.TextureCameraNoShadows)
                {
                    var d = c.GetComponent<UniversalAdditionalCameraData>();
                    if (d != null && d.renderShadows && !ShadowOff.ContainsKey(d)) { ShadowOff[d] = true; d.renderShadows = false; }
                }
                if (interval <= 0) continue;
                LastRender.TryGetValue(c, out float last);
                if (now - last < interval - 0.002f) { c.enabled = false; Held.Add(c); }
                else LastRender[c] = now;
            }
        }

        /// <summary>Very early in the next frame: the game sees its cameras enabled again.</summary>
        public static void ReleaseTextureCameras()
        {
            foreach (var c in Held) if (c != null) c.enabled = true;
            Held.Clear();
        }

        public static void RestoreTextureCameras()
        {
            ReleaseTextureCameras();
            foreach (var d in ShadowOff.Keys) if (d != null) d.renderShadows = true;
            ShadowOff.Clear();
            LastRender.Clear();
        }

        // ---------- Anti-aliasing: TAA -> SMAA ----------

        private static UniversalAdditionalCameraData _aaCam;
        private static AntialiasingMode _aaOld;

        public static void ApplyAntiAliasing(bool smaa)
        {
            var cam = Camera.main;
            var d = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
            if (!smaa || d != _aaCam) RestoreAntiAliasing();
            if (!smaa || d == null) return;
            if (_aaCam == null)
            {
                if (d.antialiasing != AntialiasingMode.TemporalAntiAliasing) return;
                _aaCam = d;
                _aaOld = d.antialiasing;
            }
            if (d.antialiasing == AntialiasingMode.TemporalAntiAliasing)
            {
                d.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                d.antialiasingQuality = AntialiasingQuality.High;
            }
        }

        public static void RestoreAntiAliasing()
        {
            if (_aaCam != null && _aaCam.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing) _aaCam.antialiasing = _aaOld;
            _aaCam = null;
        }
    }
}
