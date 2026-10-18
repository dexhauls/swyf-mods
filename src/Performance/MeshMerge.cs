using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>Office furniture merged into fewer renderers (diagnostics first).</summary>
    internal static class MeshMerge
    {
        public static bool Command(string[] parts, Action<string> write)
        {
            switch (parts[0])
            {
                case "deskinfo": write(DeskInfo(parts.Length > 1 ? parts[1] : "Desk")); return true;
                case "vbtest": write(VbTest(parts.Length > 1 ? parts[1] : "Desk")); return true;
                case "sbtest": write(SbTest(parts.Length > 1 ? parts[1] : "Desk")); return true;
                case "merge":
                {
                    string a = parts.Length > 1 ? parts[1] : "stats";
                    if (a == "off") StaticMerge.Configure(0);
                    else if (a == "show" || a == "hide") StaticMerge.Show(a == "show");
                    else if (a != "stats") StaticMerge.Configure(float.Parse(a, System.Globalization.CultureInfo.InvariantCulture));
                    write($"Static merge: {StaticMerge.Stats}, build {StaticMerge.BuildMs:F0} ms{(StaticMerge.LastError != null ? ", last error " + StaticMerge.LastError : "")}");
                    return true;
                }
                case "reflprobes":
                {
                    var sb = new StringBuilder("=== Reflection probes" + Environment.NewLine);
                    foreach (var p in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                        {
                        Icalls.Ready();
                        var self = Icalls.Ptr(p);
                        Icalls.ProbeBounds(self, out var b);
                        sb.AppendLine($"  {(p.isActiveAndEnabled ? "on " : "off")} {SceneReport.Path(p.transform)} imp {Icalls.ProbeImportance(self)} center {b.center} size {b.size} blend {Icalls.ProbeBlendDistance(self)} mode {p.mode}");
                    }
                    write(sb.ToString());
                    return true;
                }
                case "mergeab":
                    StaticMerge.StartAb(parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 30f, write);
                    return true;
                case "mergeshot":
                    Plugin.Instance.StartCoroutine(Shots(write));
                    return true;
                case "mergecensus": write(Census(parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 12f)); return true;
            }
            return false;
        }

        // Merged / original / merged within a few frames, so a moving player barely spoils the comparison.
        private static System.Collections.IEnumerator Shots(Action<string> write)
        {
            string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "perf-shots");
            System.IO.Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("HHmmss");
            StaticMerge.Show(true);
            yield return Plugin.Capture(System.IO.Path.Combine(dir, $"{stamp}-merge-a.png"));
            StaticMerge.Show(false);
            yield return Plugin.Capture(System.IO.Path.Combine(dir, $"{stamp}-merge-b-orig.png"));
            StaticMerge.Show(true);
            yield return Plugin.Capture(System.IO.Path.Combine(dir, $"{stamp}-merge-c.png"));
            write($"Merge screenshots: perf-shots/{stamp}-merge-*.png (a, c merged; b original)");
        }

        private static readonly string[] Blocked = { "NetworkIdentity", "Rigidbody", "Animator", "Animation", "ScenePickup", "Outline", "LODGroup", "ParticleSystem", "Cloth" };

        // Why this renderer must stay on its own, or null when it can be merged.
        internal static string Exclusion(Renderer r, MaterialPropertyBlock block)
        {
            if (!(r is MeshRenderer)) return "not a MeshRenderer";
            if (r.lightProbeUsage == LightProbeUsage.CustomProvided) return "custom probe lighting";
            r.GetPropertyBlock(block);
            if (!block.isEmpty) return "property block";
            var f = r.GetComponent<MeshFilter>();
            var mesh = f != null ? f.sharedMesh : null;
            if (mesh == null) return "no mesh";
            var mats = r.sharedMaterials;
            if (mats.Length != mesh.subMeshCount) return "materials != submeshes";
            foreach (var m in mats)
            {
                if (m == null) return "empty material";
                if (m.renderQueue >= 2450) return "transparent";
            }
            for (var t = r.transform; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (Array.IndexOf(Blocked, n) >= 0 || n.Contains("Interactable") || n.Contains("Pickup") || n.Contains("Door")) return "under " + n;
                }
            return null;
        }

        internal static string GroupKey(Renderer r, float cell)
        {
            var c = r.bounds.center;
            string pos = cell > 0 ? $"{Mathf.FloorToInt(c.x / cell)},{Mathf.FloorToInt(c.y / 3f)},{Mathf.FloorToInt(c.z / cell)}" : "";
            return string.Join(",", r.sharedMaterials.Select(m => m.GetInstanceID())) + $"|{r.lightmapIndex}|{r.shadowCastingMode}|{r.gameObject.layer}|{pos}";
        }

        private static string Census(float cell)
        {
            var block = new MaterialPropertyBlock();
            var all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer)).ToList();
            var reasons = new Dictionary<string, List<Renderer>>();
            var ok = new List<Renderer>();
            foreach (var r in all)
            {
                string why = Exclusion(r, block);
                if (why == null) { ok.Add(r); continue; }
                if (!reasons.TryGetValue(why, out var l)) reasons[why] = l = new List<Renderer>();
                l.Add(r);
            }
            var sb = new StringBuilder();
            sb.AppendLine($"=== Merge census {DateTime.Now:HH:mm:ss}: {all.Count} renderers (all floors), {ok.Count} mergeable, {all.Count(r => r.forceRenderingOff)} hidden by floor streaming");
            foreach (var c in new[] { cell, 0f })
            {
                var groups = ok.GroupBy(r => GroupKey(r, c)).ToList();
                sb.AppendLine($"  cell {(c > 0 ? c + " m" : "none")}: {groups.Count} groups ({groups.Count(g => g.Count() > 1)} with 2+ renderers holding {groups.Where(g => g.Count() > 1).Sum(g => g.Count())}) → saves {ok.Count - groups.Count} renderers");
            }
            sb.AppendLine("  Mergeable by owner:");
            foreach (var g in ok.GroupBy(r => Owner(r)).OrderByDescending(g => g.Count()).Take(20))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}  lightmapped {g.Count(r => r.lightmapIndex >= 0 && r.lightmapIndex < 65534)}  mats {g.Select(r => r.sharedMaterial).Distinct().Count()}");
            sb.AppendLine("  Kept separate:");
            foreach (var kv in reasons.OrderByDescending(k => k.Value.Count))
                sb.AppendLine($"    {kv.Value.Count,5}  {kv.Key}: {string.Join(", ", kv.Value.GroupBy(Owner).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + " " + g.Count()))}");
            // Do the extra UV channels hold object-space data (they'd need the same transform as positions/normals)?
            sb.AppendLine("  Extra channels (max |uv - position| / |uv.xyz - normal|):");
            foreach (var mesh in ok.Select(r => r.GetComponent<MeshFilter>().sharedMesh).Distinct().Take(25))
            {
                var d = MeshData.Read(mesh, out string why);
                if (d == null) { sb.AppendLine($"    {mesh.name}: {why}"); continue; }
                var parts = new List<string>();
                for (int a = 5; a < MeshData.AttrCount; a++)
                {
                    if (d.Attr[a] == null) continue;
                    float dp = Diff(d, a, 0), dn = Diff(d, a, 1);
                    float mn = d.Attr[a].Min(), mx = d.Attr[a].Max();
                    parts.Add($"uv{a - 4}x{d.Dim[a]} range {mn:F2}..{mx:F2} vsPos {dp:F3} vsNrm {dn:F3}");
                }
                sb.AppendLine($"    {mesh.name}: {string.Join("; ", parts)}");
            }
            return sb.ToString();
        }

        private static float Diff(MeshData d, int a, int b)
        {
            int da = d.Dim[a], db = d.Dim[b], n = Math.Min(Math.Min(da, db), 3);
            float max = 0;
            for (int i = 0; i < d.VertexCount; i++)
                for (int c = 0; c < n; c++) max = Math.Max(max, Math.Abs(d.Attr[a][i * da + c] - d.Attr[b][i * db + c]));
            return max;
        }

        internal static string Owner(Renderer r)
        {
            var chain = new List<Transform>();
            for (var t = r.transform; t != null; t = t.parent) chain.Add(t);
            chain.Reverse();
            foreach (var t in chain)
                if (t.name.Contains("(Clone)") || t.name.EndsWith(")")) return Clean(t.name);
            return Clean(chain[Math.Min(chain.Count - 1, 3)].name);
        }

        internal static string Clean(string n)
        {
            n = n.Replace("(Clone)", "").Trim();
            int i = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (i > 0 && n.EndsWith(")")) n = n.Substring(0, i);
            return n;
        }

        // The top-most ancestor called <name> closest to the camera.
        private static Transform Nearest(string name)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            Transform best = null;
            float bd = float.MaxValue;
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                Transform root = null;
                for (var t = r.transform; t != null; t = t.parent) if (Clean(t.name) == name) root = t;
                if (root == null) continue;
                float d = (root.position - cam.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = root; }
            }
            return best;
        }

        private static string DeskInfo(string name)
        {
            var root = Nearest(name);
            if (root == null) return "No " + name + " found.";
            var sb = new StringBuilder();
            sb.AppendLine($"=== {name} info {DateTime.Now:HH:mm:ss}: {SceneReport.Path(root)} at {root.position}, icalls {(Icalls.Ready() ? "ok" : Icalls.Error)}");
            var block = new MaterialPropertyBlock();
            void Walk(Transform t, int depth)
            {
                string pad = new string(' ', depth * 2);
                var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
                sb.AppendLine($"{pad}{t.name} [{(t.gameObject.activeSelf ? "" : "INACTIVE ")}scale {t.localScale}] {string.Join(", ", comps)}");
                var r = t.GetComponent<Renderer>();
                if (r != null)
                {
                    r.GetPropertyBlock(block);
                    var f = t.GetComponent<MeshFilter>();
                    var m = f != null ? f.sharedMesh : null;
                    string mesh = m == null ? "no mesh" : $"mesh '{m.name}' v{m.vertexCount} sub{m.subMeshCount} readable {m.isReadable}";
                    if (m != null && Icalls.Ready())
                    {
                        var self = Icalls.Ptr(m);
                        var attrs = new List<string>();
                        for (int a = 0; a < 14; a++)
                            if (m.HasVertexAttribute((VertexAttribute)a))
                                attrs.Add($"{(VertexAttribute)a}:{(VertexAttributeFormat)Icalls.AttrFormat(self, a)}x{Icalls.AttrDimension(self, a)}@s{Icalls.AttrStream(self, a)}+{Icalls.AttrOffset(self, a)}");
                        mesh += $" streams {Icalls.MeshVertexBufferCount(self)} [{string.Join(" ", attrs)}]";
                    }
                    sb.AppendLine($"{pad}  > {r.GetType().Name} {(r.enabled ? "on" : "OFF")}{(r.forceRenderingOff ? " forceOff" : "")} {mesh}");
                    sb.AppendLine($"{pad}    mats [{string.Join(" | ", r.sharedMaterials.Select(x => x == null ? "null" : x.name + "/" + x.shader.name))}] shadow {r.shadowCastingMode} lm {r.lightmapIndex} probes {r.lightProbeUsage} mpb {(block.isEmpty ? "-" : "YES")} layer {t.gameObject.layer} static-batch {r.isPartOfStaticBatch} det {Det(t.localToWorldMatrix):F3}");
                }
                foreach (Transform c in t) Walk(c, depth + 1);
            }
            Walk(root, 0);
            return sb.ToString();
        }

        internal static float Det(Matrix4x4 m) =>
            m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        private static string VbTest(string name)
        {
            var root = Nearest(name);
            if (root == null) return "No " + name + " found.";
            var sb = new StringBuilder();
            sb.AppendLine($"=== GPU readback test {DateTime.Now:HH:mm:ss} on {SceneReport.Path(root)}");
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = f.sharedMesh;
                if (m == null) continue;
                var t0 = DateTime.Now;
                var d = MeshData.Read(m, out string why);
                double ms = (DateTime.Now - t0).TotalMilliseconds;
                if (d == null) { sb.AppendLine($"  {m.name}: FAILED {why}"); continue; }
                var p = d.Attr[0];
                Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
                for (int i = 0; i < d.VertexCount; i++)
                {
                    var v = new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
                    mn = Vector3.Min(mn, v);
                    mx = Vector3.Max(mx, v);
                }
                var b = m.bounds;
                float err = (mn - b.min).magnitude + (mx - b.max).magnitude;
                sb.AppendLine($"  {m.name}: {d.VertexCount} v, tris {string.Join("+", d.Indices.Select(x => x.Length / 3))}, [{d.Signature()}] bounds err {err:F5} {(err < 0.001f * (1 + b.size.magnitude) ? "OK" : "MISMATCH " + mn + mx + " vs " + b.min + b.max)} {ms:F1} ms");
            }
            return sb.ToString();
        }

        private static string SbTest(string name)
        {
            var root = Nearest(name);
            if (root == null) return "No " + name + " found.";
            if (!Icalls.Ready()) return "icalls: " + Icalls.Error;
            var gos = root.GetComponentsInChildren<MeshFilter>().Select(f => f.gameObject).ToArray();
            string before = $"{gos.Count(g => g.GetComponent<Renderer>().isPartOfStaticBatch)}/{gos.Length}";
            string err = "none";
            try { Icalls.StaticBatchCombine(gos, Icalls.Ptr(root.gameObject)); } catch (Exception e) { err = e.Message; }
            string after = $"{gos.Count(g => g.GetComponent<Renderer>().isPartOfStaticBatch)}/{gos.Length}";
            var meshes = gos.Select(g => g.GetComponent<MeshFilter>().sharedMesh).Where(m => m != null).Select(m => m.name).Distinct().Take(5);
            return $"=== Static batching test on {SceneReport.Path(root)}: part of static batch {before} -> {after}, exception {err}, meshes now {string.Join(", ", meshes)} (see Player.log for engine errors)";
        }
    }
}
