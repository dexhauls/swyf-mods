using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Performance
{
    /// <summary>Diagnostic experiments: each one switches a single expensive thing off for the A/B bench.</summary>
    internal static class Probes
    {
        private static UniversalRenderPipelineAsset Asset => GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        public static List<Bench.Experiment> Diagnostic()
        {
            var list = new List<Bench.Experiment>();
            list.Add(Asset1("render scale 0.5", a => a.renderScale, (a, v) => a.renderScale = v, 0.5f));
            list.Add(Asset1("shadow distance 0 (main light)", a => a.shadowDistance, (a, v) => a.shadowDistance = v, 0f));
            list.Add(LightsShadowOff("all light shadows off", l => true));
            list.Add(LightsShadowOff("point/spot shadows off", l => l.type != LightType.Directional));
            list.Add(LightsOff("point/spot lights off", l => l.type != LightType.Directional));
            list.Add(CamField("post-processing off", d => d.renderPostProcessing, (d, v) => d.renderPostProcessing = v, false));
            list.Add(Asset1("MSAA 1", a => a.msaaSampleCount, (a, v) => a.msaaSampleCount = v, 1));
            list.Add(Asset1("HDR off", a => a.supportsHDR, (a, v) => a.supportsHDR = v, false));
            list.Add(Features());
            list.Add(OffscreenCameras());
            list.Add(FarClip(30f));
            list.Add(FarClip(15f));
            list.Add(SkinnedOff());
            list.Add(ShadowCastersOff());
            return list;
        }

        private static Bench.Experiment Asset1<T>(string name, Func<UniversalRenderPipelineAsset, T> get, Action<UniversalRenderPipelineAsset, T> set, T value)
        {
            T old = default;
            return new Bench.Experiment(name, () => { old = get(Asset); set(Asset, value); }, () => set(Asset, old));
        }

        private static Bench.Experiment LightsShadowOff(string name, Func<Light, bool> which)
        {
            var saved = new List<(Light, LightShadows)>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.shadows != LightShadows.None && which(l)) { saved.Add((l, l.shadows)); l.shadows = LightShadows.None; }
            }, () => { foreach (var (l, s) in saved) if (l != null) l.shadows = s; });
        }

        private static Bench.Experiment LightsOff(string name, Func<Light, bool> which)
        {
            var saved = new List<Light>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.enabled && which(l)) { saved.Add(l); l.enabled = false; }
            }, () => { foreach (var l in saved) if (l != null) l.enabled = true; });
        }

        private static Bench.Experiment CamField(string name, Func<UniversalAdditionalCameraData, bool> get, Action<UniversalAdditionalCameraData, bool> set, bool value)
        {
            var saved = new List<(UniversalAdditionalCameraData, bool)>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                foreach (var c in Camera.allCameras)
                {
                    var d = c.GetComponent<UniversalAdditionalCameraData>();
                    if (d != null) { saved.Add((d, get(d))); set(d, value); }
                }
            }, () => { foreach (var (d, v) in saved) if (d != null) set(d, v); });
        }

        private static Bench.Experiment Features()
        {
            var saved = new List<ScriptableRendererFeature>();
            return new Bench.Experiment("renderer features off", () =>
            {
                saved.Clear();
                var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (!(field?.GetValue(Asset) is ScriptableRendererData[] datas)) return;
                foreach (var d in datas)
                    if (d != null)
                        foreach (var f in d.rendererFeatures)
                            if (f != null && f.isActive) { saved.Add(f); f.SetActive(false); }
            }, () => { foreach (var f in saved) if (f != null) f.SetActive(true); });
        }

        private static Bench.Experiment OffscreenCameras()
        {
            var saved = new List<Camera>();
            return new Bench.Experiment("render-texture cameras off", () =>
            {
                saved.Clear();
                foreach (var c in Camera.allCameras)
                    if (c.targetTexture != null) { saved.Add(c); c.enabled = false; }
            }, () => { foreach (var c in saved) if (c != null) c.enabled = true; });
        }

        private static Bench.Experiment FarClip(float far)
        {
            Camera cam = null;
            float old = 0;
            return new Bench.Experiment($"main camera far clip {far} m", () =>
            {
                cam = Camera.main;
                if (cam == null) return;
                old = cam.farClipPlane;
                cam.farClipPlane = Mathf.Min(old, far);
            }, () => { if (cam != null) cam.farClipPlane = old; });
        }

        private static Bench.Experiment SkinnedOff()
        {
            var saved = new List<SkinnedMeshRenderer>();
            return new Bench.Experiment("skinned meshes hidden", () =>
            {
                saved.Clear();
                foreach (var s in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
                    if (s.enabled) { saved.Add(s); s.enabled = false; }
            }, () => { foreach (var s in saved) if (s != null) s.enabled = true; });
        }

        private static Bench.Experiment ShadowCastersOff()
        {
            var saved = new List<(Renderer, ShadowCastingMode)>();
            return new Bench.Experiment("small renderers cast no shadow", () =>
            {
                saved.Clear();
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    if (r.shadowCastingMode == ShadowCastingMode.On && r.bounds.size.magnitude < 1.5f) { saved.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.Off; }
            }, () => { foreach (var (r, m) in saved) if (r != null) r.shadowCastingMode = m; });
        }

        public static List<Bench.Experiment> Round2()
        {
            var list = new List<Bench.Experiment>();
            list.Add(Asset1("cascades 2", a => a.shadowCascadeCount, (a, v) => a.shadowCascadeCount = v, 2));
            list.Add(Combo("cascades 2 + distance 40", Asset1("", a => a.shadowCascadeCount, (a, v) => a.shadowCascadeCount = v, 2), Asset1("", a => a.shadowDistance, (a, v) => a.shadowDistance = v, 40f)));
            list.Add(Asset1("shadow distance 35", a => a.shadowDistance, (a, v) => a.shadowDistance = v, 35f));
            list.Add(SizeShadowCull("size shadow cull k15 min6", 15f, 6f));
            list.Add(SizeShadowCull("size shadow cull k8 min4", 8f, 4f));
            foreach (var f in new[] { "ScreenSpaceAmbientOcclusion", "VolumetricCloudsURP", "DecalRendererFeature", "SharpenRendererFeature", "UniversalBlurFeature" })
                list.Add(Feature(f));
            list.Add(RtCamShadows());
            list.Add(FarLightShadows(20f));
            list.Add(IdleAudio());
            list.Add(CamAA());
            return list;
        }

        private static Bench.Experiment Combo(string name, params Bench.Experiment[] parts) =>
            new Bench.Experiment(name, () => { foreach (var p in parts) p.Apply(); }, () => { for (int i = parts.Length - 1; i >= 0; i--) parts[i].Revert(); });

        private static Bench.Experiment SizeShadowCull(string name, float k, float min)
        {
            var saved = new List<(Renderer, ShadowCastingMode)>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                var cam = Camera.main;
                if (cam == null) return;
                Vector3 eye = cam.transform.position;
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r.shadowCastingMode != ShadowCastingMode.On || r is SkinnedMeshRenderer) continue;
                    var b = r.bounds;
                    float size = b.size.magnitude;
                    float dist = Mathf.Sqrt(b.SqrDistance(eye));
                    if (dist > Mathf.Max(min, size * k)) { saved.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.Off; }
                }
                Plugin.Log.LogInfo($"{name}: {saved.Count} casters off");
            }, () => { foreach (var (r, m) in saved) if (r != null) r.shadowCastingMode = m; });
        }

        private static Bench.Experiment Feature(string type)
        {
            var saved = new List<ScriptableRendererFeature>();
            return new Bench.Experiment(type + " off", () =>
            {
                saved.Clear();
                var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (!(field?.GetValue(Asset) is ScriptableRendererData[] datas)) return;
                foreach (var d in datas)
                    if (d != null)
                        foreach (var f in d.rendererFeatures)
                            if (f != null && f.isActive && f.GetType().Name == type) { saved.Add(f); f.SetActive(false); }
            }, () => { foreach (var f in saved) if (f != null) f.SetActive(true); });
        }

        private static Bench.Experiment RtCamShadows()
        {
            var saved = new List<UniversalAdditionalCameraData>();
            return new Bench.Experiment("render-texture cameras: no shadows", () =>
            {
                saved.Clear();
                foreach (var c in Camera.allCameras)
                {
                    var d = c.targetTexture != null ? c.GetComponent<UniversalAdditionalCameraData>() : null;
                    if (d != null && d.renderShadows) { saved.Add(d); d.renderShadows = false; }
                }
            }, () => { foreach (var d in saved) if (d != null) d.renderShadows = true; });
        }

        private static Bench.Experiment FarLightShadows(float dist)
        {
            var saved = new List<(Light, LightShadows)>();
            return new Bench.Experiment($"point/spot shadows beyond {dist} m off", () =>
            {
                saved.Clear();
                var cam = Camera.main;
                if (cam == null) return;
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type != LightType.Directional && l.shadows != LightShadows.None && Vector3.Distance(l.transform.position, cam.transform.position) > dist)
                    { saved.Add((l, l.shadows)); l.shadows = LightShadows.None; }
            }, () => { foreach (var (l, s) in saved) if (l != null) l.shadows = s; });
        }

        private static Bench.Experiment IdleAudio()
        {
            var saved = new List<AudioSource>();
            return new Bench.Experiment("idle audio sources disabled", () =>
            {
                saved.Clear();
                foreach (var a in UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                    if (a.enabled && !a.isPlaying) { saved.Add(a); a.enabled = false; }
                Plugin.Log.LogInfo($"idle audio: {saved.Count} disabled");
            }, () => { foreach (var a in saved) if (a != null) a.enabled = true; });
        }

        private static Bench.Experiment CamAA()
        {
            UniversalAdditionalCameraData d = null;
            AntialiasingMode old = AntialiasingMode.None;
            return new Bench.Experiment("main camera TAA -> none", () =>
            {
                d = Camera.main != null ? Camera.main.GetComponent<UniversalAdditionalCameraData>() : null;
                if (d == null) return;
                old = d.antialiasing;
                d.antialiasing = AntialiasingMode.None;
            }, () => { if (d != null) d.antialiasing = old; });
        }

        public static List<Bench.Experiment> Round4() => new List<Bench.Experiment>
        {
            ExteriorLights("exterior point lights: shadows off", false),
            ExteriorLights("exterior point lights: off", true),
        };

        private static bool IsExterior(Light l) => l.type == LightType.Point && l.range > 30f && SceneReport.Path(l.transform).Contains("Exterior");

        private static Bench.Experiment ExteriorLights(string name, bool switchOff)
        {
            var saved = new List<(Light l, LightShadows s, bool e)>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (IsExterior(l)) { saved.Add((l, l.shadows, l.enabled)); if (switchOff) l.enabled = false; else l.shadows = LightShadows.None; }
                Plugin.Log.LogInfo($"{name}: {saved.Count} lights");
            }, () => { foreach (var (l, sh, e) in saved) if (l != null) { l.shadows = sh; l.enabled = e; } });
        }

        public static List<Bench.Experiment> Round3()
        {
            return new List<Bench.Experiment>
            {
                PropertyBlocks("props: own SH (MPB) -> scene light probes", true),
                PropertyBlocks("ALL property blocks removed (upper bound)", false),
            };
        }

        private static Bench.Experiment PropertyBlocks(string name, bool onlyCustomProbes)
        {
            var saved = new List<(Renderer r, MaterialPropertyBlock b, LightProbeUsage u)>();
            return new Bench.Experiment(name, () =>
            {
                saved.Clear();
                var probe = new MaterialPropertyBlock();
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r is ParticleSystemRenderer) continue;
                    r.GetPropertyBlock(probe);
                    if (probe.isEmpty) continue;
                    if (onlyCustomProbes && r.lightProbeUsage != LightProbeUsage.CustomProvided) continue;
                    var copy = new MaterialPropertyBlock();
                    r.GetPropertyBlock(copy);
                    saved.Add((r, copy, r.lightProbeUsage));
                    r.SetPropertyBlock(null);
                    if (r.lightProbeUsage == LightProbeUsage.CustomProvided) r.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
                Plugin.Log.LogInfo($"{name}: {saved.Count} renderers");
            }, () =>
            {
                foreach (var (r, b, u) in saved)
                    if (r != null) { r.lightProbeUsage = u; r.SetPropertyBlock(b); }
            });
        }
    }
}
