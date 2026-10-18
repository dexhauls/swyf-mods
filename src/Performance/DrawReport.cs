using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>
    /// Why are there so many draw calls? What the main camera actually draws: shaders and their keyword variants
    /// (each one a SetPass), material instances, renderers with MaterialPropertyBlocks (they drop out of the
    /// SRP Batcher), heaviest meshes, and whether anything beyond frustum culling hides objects (occlusion culling).
    /// </summary>
    internal static class DrawReport
    {
        public static string Build()
        {
            var sb = new StringBuilder();
            var cam = Camera.main;
            if (cam == null) return "No main camera.";
            var planes = Frustum(cam);
            var block = new MaterialPropertyBlock();
            var all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff && !(r is ParticleSystemRenderer)).ToList();
            var inFrustum = all.Where(r => InFrustum(planes, r.bounds)).ToList();
            var visible = inFrustum.Where(r => r.isVisible).ToList();
            sb.AppendLine($"=== Draw anatomy {DateTime.Now:HH:mm:ss} at {cam.transform.position} looking {cam.transform.forward}");
            sb.AppendLine($"Renderers enabled {all.Count}, in camera frustum {inFrustum.Count}, of those visible {visible.Count} " +
                          $"(occlusion culling {(cam.useOcclusionCulling ? "on" : "off")}: frustum-but-hidden {inFrustum.Count - visible.Count})");
            sb.AppendLine($"Batches now {FrameStats.Batches}, SetPass {FrameStats.SetPass}, draws {FrameStats.DrawCalls}, tris {FrameStats.Triangles}");

            int subTotal = 0, mpb = 0, nullMat = 0;
            var shaders = new Dictionary<string, int>();
            var variants = new Dictionary<string, int>();
            var mats = new HashSet<Material>();
            var matNames = new Dictionary<string, HashSet<Material>>();
            var meshCost = new Dictionary<Mesh, (int count, long tris)>();
            var mpbOwners = new Dictionary<string, int>();
            foreach (var r in inFrustum)
            {
                r.GetPropertyBlock(block);
                if (!block.isEmpty) { mpb++; Bump(mpbOwners, Owner(r)); }
                var ms = r.sharedMaterials;
                subTotal += ms.Length;
                foreach (var m in ms)
                {
                    if (m == null) { nullMat++; continue; }
                    mats.Add(m);
                    string sh = m.shader != null ? m.shader.name : "?";
                    Bump(shaders, sh);
                    Bump(variants, sh + " [" + string.Join(" ", m.shaderKeywords.OrderBy(k => k)) + "]");
                    string baseName = m.name.Replace(" (Instance)", "");
                    if (!matNames.TryGetValue(baseName, out var set)) matNames[baseName] = set = new HashSet<Material>();
                    set.Add(m);
                }
                if (r is MeshRenderer)
                {
                    var f = r.GetComponent<MeshFilter>();
                    var mesh = f != null ? f.sharedMesh : null;
                    if (mesh != null)
                    {
                        long t = 0;
                        for (int i = 0; i < mesh.subMeshCount; i++) t += (long)mesh.GetIndexCount(i) / 3;
                        meshCost.TryGetValue(mesh, out var c);
                        meshCost[mesh] = (c.count + 1, t);
                    }
                }
            }
            sb.AppendLine($"In frustum: {subTotal} material slots (≈ draw calls before shadows), {mats.Count} distinct materials, {shaders.Count} shaders, {variants.Count} shader+keyword variants, {mpb} renderers with MaterialPropertyBlock, {nullMat} empty slots");
            sb.AppendLine("Shaders (material slots):");
            foreach (var kv in shaders.OrderByDescending(k => k.Value).Take(15)) sb.AppendLine($"  {kv.Value,6}  {kv.Key}");
            sb.AppendLine("Most common shader variants:");
            foreach (var kv in variants.OrderByDescending(k => k.Value).Take(12)) sb.AppendLine($"  {kv.Value,6}  {Trim(kv.Key, 150)}");
            sb.AppendLine("Materials with many instances (same name, different objects = copies made by .material):");
            foreach (var kv in matNames.Where(k => k.Value.Count > 3).OrderByDescending(k => k.Value.Count).Take(12)) sb.AppendLine($"  {kv.Value.Count,6} copies  {kv.Key}");
            if (mpb > 0)
            {
                sb.AppendLine("Renderers with MaterialPropertyBlock by owner:");
                foreach (var kv in mpbOwners.OrderByDescending(k => k.Value).Take(10)) sb.AppendLine($"  {kv.Value,6}  {kv.Key}");
            }
            sb.AppendLine("Heaviest meshes in frustum (triangles × copies):");
            foreach (var kv in meshCost.OrderByDescending(k => k.Value.tris * k.Value.count).Take(15))
                sb.AppendLine($"  {kv.Value.tris * kv.Value.count,10:N0} = {kv.Value.tris,8:N0} × {kv.Value.count,4}  {kv.Key.name}");
            sb.AppendLine("Renderers in frustum by object type (top-level name below the map):");
            foreach (var g in inFrustum.GroupBy(Owner).OrderByDescending(g => g.Sum(r => r.sharedMaterials.Length)).Take(15))
                sb.AppendLine($"  {g.Sum(r => r.sharedMaterials.Length),6} slots {g.Count(),5} renderers  {g.Key}");
            return sb.ToString();
        }

        // GeometryUtility's frustum helpers drag in Span<> overloads net472 can't resolve: Gribb–Hartmann by hand.
        private static Vector4[] Frustum(Camera cam)
        {
            var m = cam.projectionMatrix * cam.worldToCameraMatrix;
            var rows = new[] { m.GetRow(0), m.GetRow(1), m.GetRow(2), m.GetRow(3) };
            return new[] { rows[3] + rows[0], rows[3] - rows[0], rows[3] + rows[1], rows[3] - rows[1], rows[3] + rows[2], rows[3] - rows[2] };
        }

        private static bool InFrustum(Vector4[] planes, Bounds b)
        {
            foreach (var p in planes)
            {
                var n = new Vector3(p.x, p.y, p.z);
                // The corner furthest along the plane normal.
                var c = b.center + Vector3.Scale(b.extents, new Vector3(Mathf.Sign(n.x), Mathf.Sign(n.y), Mathf.Sign(n.z)));
                if (Vector3.Dot(n, c) + p.w < 0) return false;
            }
            return true;
        }

        private static void Bump(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int v); d[k] = v + 1; }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        // A stable "kind of object" name: the nearest ancestor that looks like a prefab instance, else the 3rd level.
        private static string Owner(Renderer r)
        {
            var chain = new List<Transform>();
            for (var t = r.transform; t != null; t = t.parent) chain.Add(t);
            chain.Reverse();
            foreach (var t in chain)
                if (t.name.Contains("(Clone)") || t.name.EndsWith(")")) return Clean(t.name);
            return Clean(chain[Math.Min(chain.Count - 1, 3)].name);
        }

        private static string Clean(string n)
        {
            n = n.Replace("(Clone)", "").Trim();
            int i = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (i > 0 && n.EndsWith(")")) n = n.Substring(0, i);
            return n;
        }
    }
}
