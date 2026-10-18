using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace Performance
{
    /// <summary>What the game renders and runs right now: display, URP, cameras, lights, renderers, scripts.</summary>
    internal static class SceneReport
    {
        public static string Build()
        {
            var sb = new StringBuilder();
            Display(sb);
            Pipeline(sb);
            Cameras(sb);
            Lights(sb);
            Renderers(sb);
            Misc(sb);
            Behaviours(sb);
            return sb.ToString();
        }

        private static void Display(StringBuilder sb)
        {
            sb.AppendLine($"Scene: {string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name))}");
            sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB), CPU: {SystemInfo.processorType} x{SystemInfo.processorCount}");
            sb.AppendLine($"Screen {Screen.width}x{Screen.height} {Screen.fullScreenMode}, display {Screen.currentResolution}, vSync {QualitySettings.vSyncCount}, targetFrameRate {Application.targetFrameRate}, runInBackground {Application.runInBackground}, focused {Application.isFocused}");
            sb.AppendLine($"Quality level {QualitySettings.GetQualityLevel()} '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', lodBias {QualitySettings.lodBias}, maxLOD {QualitySettings.maximumLODLevel}, game setting '{SafeGameQuality()}'");
        }

        private static string SafeGameQuality()
        {
            try { return SettingsUi.Instance != null ? SettingsUi.Instance.GraphicsQuality : "?"; } catch { return "?"; }
        }

        private static void Pipeline(StringBuilder sb)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) { sb.AppendLine($"Pipeline: {GraphicsSettings.currentRenderPipeline}"); return; }
            sb.AppendLine($"URP asset '{asset.name}': renderScale {asset.renderScale}, upscaling {asset.upscalingFilter}, MSAA {asset.msaaSampleCount}, HDR {asset.supportsHDR}, depthTex {asset.supportsCameraDepthTexture}, opaqueTex {asset.supportsCameraOpaqueTexture}, SRP batcher {asset.useSRPBatcher}");
            sb.AppendLine($"  shadows: main {asset.supportsMainLightShadows} res {asset.mainLightShadowmapResolution}, distance {asset.shadowDistance}, cascades {asset.shadowCascadeCount}, soft {asset.supportsSoftShadows}; additional lights {asset.additionalLightsRenderingMode} max {asset.maxAdditionalLightsCount} shadows {asset.supportsAdditionalLightShadows} atlas {asset.additionalLightsShadowmapResolution}");
            try
            {
                var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(asset) as ScriptableRendererData[];
                if (list != null)
                    foreach (var data in list)
                    {
                        if (data == null) continue;
                        sb.AppendLine($"  renderer '{data.name}' ({data.GetType().Name}): features " + string.Join(", ", data.rendererFeatures.Where(f => f != null).Select(f => $"{f.name}[{f.GetType().Name}]{(f.isActive ? "" : " off")}")));
                        if (data is UniversalRendererData u) sb.AppendLine($"    rendering path {u.renderingMode}, depth priming {u.depthPrimingMode}, copy depth {u.copyDepthMode}");
                    }
            }
            catch (Exception e) { sb.AppendLine("  renderer list: " + e.Message); }
            foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (!v.isActiveAndEnabled) continue;
                var p = v.sharedProfile;
                sb.AppendLine($"  volume '{Path(v.transform)}' global {v.isGlobal} weight {v.weight}: " + (p == null ? "no profile" : string.Join(", ", p.components.Where(c => c != null && c.active).Select(c => c.GetType().Name))));
            }
        }

        private static void Cameras(StringBuilder sb)
        {
            var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"Cameras: {all.Length} total, {all.Count(c => c.isActiveAndEnabled)} active");
            foreach (var c in all.OrderByDescending(c => c.isActiveAndEnabled))
            {
                var d = c.GetComponent<UniversalAdditionalCameraData>();
                string rt = c.targetTexture != null ? $"RT {c.targetTexture.width}x{c.targetTexture.height}" : "screen";
                sb.AppendLine($"  {(c.isActiveAndEnabled ? "ON " : "off")} '{Path(c.transform)}' depth {c.depth} {rt} far {c.farClipPlane} mask {c.cullingMask:X} " +
                              (d == null ? "" : $"{d.renderType} post {d.renderPostProcessing} AA {d.antialiasing} shadows {d.renderShadows} stack {d.cameraStack?.Count ?? 0}"));
            }
        }

        private static void Lights(StringBuilder sb)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var on = lights.Where(l => l.isActiveAndEnabled).ToArray();
            var cam = Camera.main;
            sb.AppendLine($"Lights: {lights.Length} in scene, {on.Length} enabled; shadowed {on.Count(l => l.shadows != LightShadows.None)}; by type " +
                          string.Join(", ", on.GroupBy(l => l.type).Select(g => $"{g.Key} {g.Count()} ({g.Count(l => l.shadows != LightShadows.None)} shadowed)")));
            foreach (var l in on.OrderByDescending(l => l.shadows != LightShadows.None).ThenBy(l => cam ? (l.transform.position - cam.transform.position).sqrMagnitude : 0).Take(30))
                sb.AppendLine($"  {l.type} '{Path(l.transform)}' shadows {l.shadows} range {l.range:F1} int {l.intensity:F2} dist {(cam ? Vector3.Distance(cam.transform.position, l.transform.position) : 0):F1}");
            var probes = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
            sb.AppendLine($"Reflection probes: {probes.Length} (" + string.Join(", ", probes.GroupBy(p => p.mode + "/" + p.refreshMode).Select(g => g.Key + " " + g.Count())) + ")");
        }

        private static void Renderers(StringBuilder sb)
        {
            var all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var on = all.Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            sb.AppendLine($"Renderers: {all.Length} active objects, {on.Length} enabled, visible last frame {on.Count(r => r.isVisible)}, casting shadows {on.Count(r => r.shadowCastingMode != ShadowCastingMode.Off)}");
            sb.AppendLine("  by type: " + string.Join(", ", on.GroupBy(r => r.GetType().Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()} (vis {g.Count(r => r.isVisible)})")));
            var smr = on.OfType<SkinnedMeshRenderer>().ToArray();
            sb.AppendLine($"  skinned: {smr.Length}, visible {smr.Count(s => s.isVisible)}");
            long tris = 0;
            foreach (var r in on)
                if (r.isVisible && r is MeshRenderer)
                {
                    var f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) { var m = f.sharedMesh; for (int i = 0; i < m.subMeshCount; i++) tris += (long)m.GetIndexCount(i) / 3; }
                }
            sb.AppendLine($"  visible static-mesh triangles ~{tris:N0}; LODGroups {UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None).Length}");
            sb.AppendLine("  enabled renderers by top-level object (top 25):");
            foreach (var g in on.GroupBy(r => Root(r.transform, 2)).OrderByDescending(g => g.Count()).Take(25))
                sb.AppendLine($"    {g.Count(),6} (vis {g.Count(r => r.isVisible),5}, shadow {g.Count(r => r.shadowCastingMode != ShadowCastingMode.Off),5})  {g.Key}");
        }

        private static void Misc(StringBuilder sb)
        {
            var ps = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            var anim = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).Where(a => a.isActiveAndEnabled).ToArray();
            var audio = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            var video = UnityEngine.Object.FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            sb.AppendLine($"Particles: {ps.Length} ({ps.Count(p => p.isPlaying)} playing). Animators: {anim.Length} enabled");
            sb.AppendLine($"Audio: {audio.Length} sources ({audio.Count(a => a.isPlaying)} playing). Video: {video.Length} ({video.Count(v => v.isPlaying)} playing: {string.Join(", ", video.Where(v => v.isPlaying).Select(v => Path(v.transform)))})");
            sb.AppendLine($"UI: {docs.Length} UIDocuments ({docs.Count(d => d.isActiveAndEnabled)} on), {canvases.Length} canvases ({canvases.Count(c => c.isActiveAndEnabled)} on)");
            var panels = new HashSet<PanelSettings>(docs.Where(d => d.isActiveAndEnabled && d.panelSettings != null).Select(d => d.panelSettings));
            foreach (var p in panels)
                sb.AppendLine($"  panel '{p.name}' target {(p.targetTexture != null ? p.targetTexture.width + "x" + p.targetTexture.height : "screen")} docs {docs.Count(d => d.panelSettings == p && d.isActiveAndEnabled)}");
            var rts = Resources.FindObjectsOfTypeAll<RenderTexture>();
            sb.AppendLine($"RenderTextures: {rts.Length}, created {rts.Count(r => r.IsCreated())}: " + string.Join(", ", rts.Where(r => r.IsCreated()).GroupBy(r => r.width + "x" + r.height).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} x{g.Count()}")));
        }

        private static void Behaviours(StringBuilder sb)
        {
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).Where(b => b != null && b.isActiveAndEnabled).ToArray();
            sb.AppendLine($"Enabled MonoBehaviours: {all.Length}. Most common types with Update/LateUpdate:");
            foreach (var g in all.GroupBy(b => b.GetType()).Where(g => HasUpdate(g.Key)).OrderByDescending(g => g.Count()).Take(25))
                sb.AppendLine($"  {g.Count(),5}  {g.Key.FullName}");
        }

        private static bool HasUpdate(Type t)
        {
            for (; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var n in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    if (t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null) return true;
            return false;
        }

        internal static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 5; t = t.parent, i++) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Root(Transform t, int depth)
        {
            var chain = new List<string>();
            for (; t != null; t = t.parent) chain.Add(t.name);
            chain.Reverse();
            return string.Join("/", chain.Take(depth));
        }
    }
}
