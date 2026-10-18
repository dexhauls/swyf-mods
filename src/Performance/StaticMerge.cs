using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Performance
{
    /// <summary>
    /// The office is built from thousands of separate renderers (walls, floor tiles, cabinets, desk parts…), each its
    /// own draw call in the camera and in every shadow pass. The game's meshes aren't readable, so Unity's static
    /// batching can't combine them at runtime; instead the vertices are copied back from the GPU (MeshData) and
    /// pieces that never move are merged per material, lightmap and ~12 m cell into one mesh each. The originals are
    /// only switched off, so the merge can be shown/hidden instantly and undone. Anything that moves, is interactive,
    /// has its own per-object lighting (property blocks, custom probes) or is transparent stays as it is.
    /// Baked lighting is kept exact: every piece's lightmap scale/offset is baked into the merged mesh's lightmap UVs.
    /// </summary>
    internal static class StaticMerge
    {
        private sealed class Part
        {
            public Renderer R;
            public Matrix4x4 Matrix;
        }

        private sealed class Group
        {
            public GameObject Go;
            public MeshRenderer Mr;
            public Mesh Mesh;
            public Part[] Parts;
        }

        private static readonly List<Group> Groups = new List<Group>();
        private static readonly HashSet<Renderer> Handled = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> Banned = new HashSet<Renderer>(); // moved or changed once: leave alone
        private static readonly Dictionary<Mesh, MeshData> Cache = new Dictionary<Mesh, MeshData>();
        private static Queue<List<Renderer>> _pending;
        private static float _cell;
        private static bool _shown;
        private static float _nextScan;
        private static int _checkCursor;
        private static GameObject _holder;
        public static int Merged, Failed, Dissolved, SkipProbe, SkipLights;
        public static float BuildMs;
        public static string LastError;

        public static string Stats => $"{Groups.Count} merged meshes from {Merged} renderers{(_pending != null ? $", building ({_pending.Count} left)" : "")}{(Failed > 0 ? $", {Failed} unreadable" : "")}{(Dissolved > 0 ? $", {Dissolved} undone" : "")}, skipped {SkipProbe} at probe edges / {SkipLights} under 9+ lights{(_shown ? "" : ", HIDDEN")}";

        /// <summary>cell ≤ 0 = off (everything undone).</summary>
        public static void Configure(float cell)
        {
            UndoOlderCopies();
            if (cell == _cell) return;
            DissolveAll();
            _cell = cell;
            _shown = cell > 0;
            _nextScan = 0;
        }

        /// <summary>Swaps between the merged meshes and the originals without rebuilding (A/B benchmark).</summary>
        public static void Show(bool show)
        {
            if (show == _shown) return;
            _shown = show;
            foreach (var g in Groups) SetGroupShown(g, show);
        }

        private static void SetGroupShown(Group g, bool show)
        {
            if (g.Mr != null) g.Mr.enabled = show;
            foreach (var p in g.Parts) if (p.R != null) p.R.enabled = !show;
        }

        public static void Tick()
        {
            if (_cell <= 0) return;
            if (_abUntil > 0) AbTick();
            if (_pending != null) { BuildSome(); return; }
            if (Time.unscaledTime > _nextScan)
            {
                _nextScan = Time.unscaledTime + 20f;
                Scan();
                return;
            }
            Check();
        }

        // ---------- building ----------

        private static void Scan()
        {
            if (Camera.main == null || !Icalls.Ready()) return;
            SkipProbe = SkipLights = 0;
            Handled.RemoveWhere(r => r == null);
            Banned.RemoveWhere(r => r == null);
            var block = new MaterialPropertyBlock();
            MapFloors();
            MapLighting();
            var groups = new Dictionary<string, List<Renderer>>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || Handled.Contains(r) || Banned.Contains(r)) continue;
                if (MeshMerge.Exclusion(r, block) != null) continue;
                if (ReflKey(r, r.bounds) == null) { SkipProbe++; continue; }
                if (LightsTouching(r.bounds, Floor(r)) > MaxLights) { SkipLights++; continue; }
                if (r.name.StartsWith("Výkon merged") || Moving.Contains(r)) continue;
                string key = Key(r);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<Renderer>();
                l.Add(r);
            }
            var todo = groups.Values.Where(l => l.Count > 1).ToList();
            if (todo.Count == 0) return;
            _pending = new Queue<List<Renderer>>(todo);
        }

        private static bool Lightmapped(Renderer r) => r.lightmapIndex >= 0 && r.lightmapIndex < 65534;

        private static ShadowCastingMode TrueShadowMode(Renderer r) => ShadowCulling.IsCulled(r) ? ShadowCastingMode.On : r.shadowCastingMode;

        private static string Key(Renderer r)
        {
            var c = r.bounds.center;
            // Probe-lit pieces take their lighting from the renderer's position: merge them only within one object.
            // The game hides whole floors (OfficeFloorStreaming): a merged mesh may only hold pieces of one floor.
            string floor = (FloorOf.TryGetValue(r, out int f) ? "F" + f : "") + ReflKey(r, r.bounds);
            string where = floor + (Lightmapped(r) || r.lightProbeUsage == LightProbeUsage.Off
                ? $"{Mathf.FloorToInt(c.x / _cell)},{Mathf.FloorToInt(c.y / 2.5f)},{Mathf.FloorToInt(c.z / _cell)}"
                : "obj" + Root(r.transform).GetInstanceID());
            var self = Icalls.Ptr(r);
            return string.Join(",", r.sharedMaterials.Select(m => m.GetInstanceID()))
                   + $"|{r.lightmapIndex}|{TrueShadowMode(r)}|{Icalls.ReceiveShadows(self)}|{r.lightProbeUsage}|{Icalls.ReflectionProbeUsage(self)}|{r.gameObject.layer}|{r.forceRenderingOff}|{where}";
        }

        private static readonly Dictionary<Renderer, int> FloorOf = new Dictionary<Renderer, int>();
        private static readonly HashSet<Renderer> Moving = new HashSet<Renderer>();

        private static void MapFloors()
        {
            FloorOf.Clear();
            Moving.Clear();
            LightFloor.Clear();
            var spawned = typeof(OfficeFloorStreaming).GetField("spawnedMovingVisuals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var s in UnityEngine.Object.FindObjectsByType<OfficeFloorStreaming>(FindObjectsSortMode.None))
            {
                int id = s.GetInstanceID() * 64;
                for (int i = 0; i < s.floors.Length; i++)
                {
                    var fl = s.floors[i];
                    foreach (var go in fl.visualObjects)
                        if (go != null) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) FloorOf[r] = id + i;
                    foreach (var r in fl.renderers) if (r != null) FloorOf[r] = id + i;
                    foreach (var li in fl.lights) if (li != null) LightFloor[li] = id + i;
                }
                var moving = new List<OfficeFloorStreaming.MovingVisual>(s.movingVisuals);
                if (spawned?.GetValue(s) is List<OfficeFloorStreaming.MovingVisual> extra) moving.AddRange(extra);
                foreach (var mv in moving)
                    if (mv?.renderers != null) foreach (var r in mv.renderers) if (r != null) Moving.Add(r);
            }
        }

        private static Transform Root(Transform t)
        {
            // The prefab-instance-looking ancestor ("Desk (12)"), like the draw report's owner.
            for (var p = t; p != null; p = p.parent)
                if (p.name.EndsWith(")")) return p;
            return t.parent != null ? t.parent : t;
        }

        // A few groups per frame so the readback (≈1 ms per new mesh) doesn't freeze the game.
        private static void BuildSome()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_pending.Count > 0 && sw.Elapsed.TotalMilliseconds < 4)
            {
                var list = _pending.Dequeue();
                try { Build(list); }
                catch (Exception e) { LastError = e.ToString(); Plugin.Log.LogWarning("Merge failed: " + e.Message); foreach (var r in list) Banned.Add(r); }
            }
            BuildMs += (float)sw.Elapsed.TotalMilliseconds;
            if (_pending.Count == 0)
            {
                _pending = null;
                Cache.Clear();
                Plugin.Log.LogInfo("Static merge: " + Stats + $", {BuildMs:F0} ms build time");
            }
        }

        private static void Build(List<Renderer> list)
        {
            // Split further by vertex layout: every merged vertex needs the same attributes.
            var bySig = new Dictionary<string, List<(Renderer r, MeshData d)>>();
            foreach (var r in list)
            {
                if (r == null || !r.enabled) continue;
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (!Cache.TryGetValue(mesh, out var d))
                {
                    d = MeshData.Read(mesh, out string why);
                    Cache[mesh] = d;
                    if (d == null) Plugin.Log.LogInfo($"Merge skips '{mesh.name}': {why}");
                }
                if (d == null) { Failed++; Banned.Add(r); continue; }
                string sig = Signature(d, Lightmapped(r));
                if (!bySig.TryGetValue(sig, out var l)) bySig[sig] = l = new List<(Renderer, MeshData)>();
                l.Add((r, d));
            }
            foreach (var l in bySig.Values) CombineChecked(l);
        }

        // The merged renderer must get the same lighting as each piece had: the same reflection probe (all pieces
        // fully inside one probe box, or all outside every box) and no more realtime lights than URP gives one
        // object (otherwise lights get dropped). If not, halve the group along its longest side and try again.
        private static void CombineChecked(List<(Renderer r, MeshData d)> l)
        {
            if (l.Count < 2) return;
            var b = l[0].r.bounds;
            foreach (var x in l) b.Encapsulate(x.r.bounds);
            string refl = ReflKey(l[0].r, l[0].r.bounds);
            if (ReflKey(l[0].r, b) == refl && LightsTouching(b, Floor(l[0].r)) <= MaxLights) { Combine(l); return; }
            var size = b.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            var sorted = l.OrderBy(x => x.r.bounds.center[axis]).ToList();
            int half = sorted.Count / 2;
            CombineChecked(sorted.GetRange(0, half));
            CombineChecked(sorted.GetRange(half, sorted.Count - half));
        }

        private struct ProbeBox { public Bounds Outer, Inner; public int Id; }
        private static readonly List<ProbeBox> Probes = new List<ProbeBox>();
        private static readonly List<(Vector3 pos, float range, bool on, int floor)> Lights = new List<(Vector3, float, bool, int)>();
        private static readonly Dictionary<Light, int> LightFloor = new Dictionary<Light, int>();
        private static int MaxLights = 8;

        private static void MapLighting()
        {
            Probes.Clear();
            Lights.Clear();
            foreach (var p in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
            {
                if (!p.isActiveAndEnabled) continue;
                var self = Icalls.Ptr(p);
                Icalls.ProbeBounds(self, out var outer);
                float blend = Icalls.ProbeBlendDistance(self);
                var inner = outer;
                inner.Expand(-2f * (blend + 0.1f));
                outer.Expand(0.2f);
                Probes.Add(new ProbeBox { Outer = outer, Inner = inner, Id = p.GetInstanceID() });
            }
            // Point/spot lights that are on, plus the ones floor streaming switches on with their floor. Area lights are baked only.
            foreach (var li in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (li.type == LightType.Point || li.type == LightType.Spot)
                    Lights.Add((li.transform.position, li.range, li.isActiveAndEnabled, LightFloor.TryGetValue(li, out int f) ? f : int.MinValue));
            var asset = GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            MaxLights = asset != null ? Math.Min(8, asset.maxAdditionalLightsCount) : 8;
        }

        // Which reflection probe a renderer with these bounds gets. Unity weighs a probe by where the bounds' centre
        // lies: inside the box minus its blend distance = only that probe, outside = none (the sky). "in<id>", "sky",
        // or null when the centre is in a blend band (a mix that a merged renderer couldn't reproduce).
        private static string ReflKey(Renderer r, Bounds b)
        {
            if (Icalls.ReflectionProbeUsage(Icalls.Ptr(r)) == 0) return "off";
            var c = b.center;
            string key = "sky";
            foreach (var p in Probes)
            {
                if (p.Inner.Contains(c)) { if (key != "sky") return null; key = "in" + p.Id; }
                else if (p.Outer.Contains(c)) return null;
            }
            return key;
        }

        private static int LightsTouching(Bounds b, int floor)
        {
            int n = 0;
            foreach (var (pos, range, on, lf) in Lights)
                if ((on || (lf == floor && floor != int.MinValue)) && b.SqrDistance(pos) < range * range) n++;
            return n;
        }

        private static int Floor(Renderer r) => FloorOf.TryGetValue(r, out int f) ? f : int.MinValue;

        private static string Signature(MeshData d, bool lightmapped)
        {
            var s = new System.Text.StringBuilder();
            for (int a = 0; a < MeshData.AttrCount; a++)
            {
                if (lightmapped && a == 5) continue; // lightmap UVs are rebuilt anyway
                if (a == 3 && d.Attr[a] != null) s.Append(d.Format[a] == 2 ? "C32," : "C,");
                else if (d.Attr[a] != null) s.Append(a).Append(':').Append(Math.Min(d.Dim[a], a == 1 ? 3 : 4)).Append(',');
            }
            return s.ToString();
        }

        private static void Combine(List<(Renderer r, MeshData d)> items)
        {
            var first = items[0];
            var proto = first.d;
            bool lm = Lightmapped(first.r);
            int total = items.Sum(i => i.d.VertexCount);
            int subs = proto.Indices.Length;

            var pos = new List<Vector3>(total);
            var nrm = proto.Attr[1] != null ? new List<Vector3>(total) : null;
            var tan = proto.Attr[2] != null ? new List<Vector4>(total) : null;
            var col = proto.Attr[3] != null ? new List<Color32>(total) : null;
            var uvs = new List<Vector4>[8];
            for (int u = 0; u < 8; u++) if (proto.Attr[4 + u] != null || (u == 1 && lm)) uvs[u] = new List<Vector4>(total);
            var tris = new List<int>[subs];
            for (int s = 0; s < subs; s++) tris[s] = new List<int>();

            var parts = new Part[items.Count];
            for (int k = 0; k < items.Count; k++)
            {
                var (r, d) = items[k];
                var m = r.transform.localToWorldMatrix;
                var nm = m.inverse.transpose;
                bool flip = MeshMerge.Det(m) < 0;
                int baseV = pos.Count, n = d.VertexCount;
                Vector4 st = new Vector4(1, 1, 0, 0);
                if (lm) Icalls.GetLightmapST(Icalls.Ptr(r), 0, out st);
                for (int i = 0; i < n; i++)
                {
                    pos.Add(m.MultiplyPoint3x4(V3(d, 0, i)));
                    if (nrm != null) nrm.Add(nm.MultiplyVector(V3(d, 1, i)).normalized);
                    if (tan != null)
                    {
                        var t = V4(d, 2, i);
                        var tw = m.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                        tan.Add(new Vector4(tw.x, tw.y, tw.z, flip ? -t.w : t.w));
                    }
                    if (col != null) { var c = V4(d, 3, i); col.Add(new Color32(B(c.x), B(c.y), B(c.z), B(c.w))); }
                    for (int u = 0; u < 8; u++)
                    {
                        if (uvs[u] == null) continue;
                        if (u == 1 && lm)
                        {
                            // Lightmap UVs come from UV1, or UV0 when the mesh has none (Unity's fallback).
                            var src = d.Attr[5] != null ? V4(d, 5, i) : V4(d, 4, i);
                            uvs[u].Add(new Vector4(src.x * st.x + st.z, src.y * st.y + st.w, 0, 0));
                        }
                        else if (u == 3 && d.Dim[7] == 3)
                        {
                            // UV3 holds smoothed normals for the outline shader: a direction like the normal.
                            uvs[u].Add(nm.MultiplyVector(V3(d, 7, i)).normalized);
                        }
                        else uvs[u].Add(V4(d, 4 + u, i));
                    }
                }
                for (int s = 0; s < subs; s++)
                {
                    var src = d.Indices[s];
                    var dst = tris[s];
                    for (int i = 0; i < src.Length; i += 3)
                    {
                        dst.Add(baseV + src[i]);
                        if (flip) { dst.Add(baseV + src[i + 2]); dst.Add(baseV + src[i + 1]); }
                        else { dst.Add(baseV + src[i + 1]); dst.Add(baseV + src[i + 2]); }
                    }
                }
                parts[k] = new Part { R = r, Matrix = m };
            }

            var mesh = new Mesh { name = "Výkon merged " + first.r.sharedMaterial.name };
            if (total > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            if (nrm != null) mesh.SetNormals(nrm);
            if (tan != null) mesh.SetTangents(tan);
            if (col != null) mesh.SetColors(col);
            for (int u = 0; u < 8; u++)
            {
                if (uvs[u] == null) continue;
                int dim = u == 1 && lm ? 2 : proto.Dim[4 + u];
                if (dim == 2) mesh.SetUVs(u, uvs[u].Select(v => new Vector2(v.x, v.y)).ToList());
                else if (dim == 3) mesh.SetUVs(u, uvs[u].Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                else mesh.SetUVs(u, uvs[u]);
            }
            mesh.subMeshCount = subs;
            for (int s = 0; s < subs; s++) mesh.SetTriangles(tris[s], s, false, 0);
            mesh.RecalculateBounds();

            if (_holder == null) _holder = new GameObject("Výkon merged statics");
            var go = new GameObject(mesh.name);
            go.transform.SetParent(_holder.transform, false);
            go.layer = first.r.gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var self = Icalls.Ptr(first.r);
            mr.sharedMaterials = first.r.sharedMaterials;
            mr.shadowCastingMode = TrueShadowMode(first.r);
            mr.receiveShadows = Icalls.ReceiveShadows(self);
            mr.lightProbeUsage = first.r.lightProbeUsage;
            mr.reflectionProbeUsage = (ReflectionProbeUsage)Icalls.ReflectionProbeUsage(self);
            mr.allowOcclusionWhenDynamic = false; // not in the baked occlusion data: big dynamic boxes get culled wrongly
            mr.forceRenderingOff = first.r.forceRenderingOff;
            if (lm)
            {
                mr.lightmapIndex = first.r.lightmapIndex;
                var one = new Vector4(1, 1, 0, 0);
                Icalls.SetLightmapST(Icalls.Ptr(mr), ref one, 0);
            }
            var g = new Group { Go = go, Mr = mr, Mesh = mesh, Parts = parts };
            Groups.Add(g);
            foreach (var p in parts) Handled.Add(p.R);
            Merged += parts.Length;
            SetGroupShown(g, _shown);
        }

        private static byte B(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

        private static Vector3 V3(MeshData d, int a, int i)
        {
            var v = d.Attr[a];
            int dim = d.Dim[a], o = i * dim;
            return new Vector3(v[o], dim > 1 ? v[o + 1] : 0, dim > 2 ? v[o + 2] : 0);
        }

        private static Vector4 V4(MeshData d, int a, int i)
        {
            var v = d.Attr[a];
            int dim = d.Dim[a], o = i * dim;
            return new Vector4(v[o], dim > 1 ? v[o + 1] : 0, dim > 2 ? v[o + 2] : 0, dim > 3 ? v[o + 3] : (a == 2 ? 1 : 0));
        }

        // ---------- interleaved A/B: merged and original alternate every 8 frames, so both see the same views ----------

        private sealed class AbStats { public List<double> Dt = new List<double>(), Main = new List<double>(), Render = new List<double>(); public double Draws, Batches, SetPass, Casters, Tris; }
        private static float _abUntil;
        private static int _abFrame;
        private static AbStats[] _ab;
        private static Action<string> _abWrite;
        private static bool _abShownBefore;
        private static int _abVsync, _abTarget;

        public static void StartAb(float seconds, Action<string> write)
        {
            _ab = new[] { new AbStats(), new AbStats() };
            _abWrite = write;
            _abShownBefore = _shown;
            _abFrame = 0;
            _abUntil = Time.realtimeSinceStartup + seconds;
            Plugin.MovedFrames = 0;
            // Like the benchmark: uncapped, or a VSync/FPS cap hides any difference.
            _abVsync = QualitySettings.vSyncCount;
            _abTarget = Application.targetFrameRate;
            FrameLimiter.Release();
            FrameLimiter.Suspended = true;
            _abVsync = QualitySettings.vSyncCount;
            _abTarget = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }

        private static void AbTick()
        {
            int k = _abFrame++ % 16;
            bool on = k < 8;
            // The frame that just ended was rendered in the state set 1 frame ago; counters lag one more frame.
            if (k % 8 >= 3)
            {
                var s = _ab[on ? 1 : 0];
                s.Dt.Add(Time.unscaledDeltaTime * 1000.0);
                if (FrameStats.TimingAvailable) { s.Main.Add(FrameStats.MainMs); s.Render.Add(FrameStats.RenderMs); }
                s.Draws += FrameStats.DrawCalls; s.Batches += FrameStats.Batches; s.SetPass += FrameStats.SetPass; s.Casters += FrameStats.ShadowCasters; s.Tris += FrameStats.Triangles;
            }
            if (k == 0) Show(true);
            else if (k == 8) Show(false);
            if (Time.realtimeSinceStartup < _abUntil) return;
            _abUntil = 0;
            Show(_abShownBefore);
            QualitySettings.vSyncCount = _abVsync;
            Application.targetFrameRate = _abTarget;
            FrameLimiter.Suspended = false;
            string Line(string name, AbStats s)
            {
                int n = Math.Max(1, s.Dt.Count);
                return $"  {name,-9} {s.Dt.Count,5} frames  mean {Avg(s.Dt):F2} ms  median {Median(s.Dt):F2}  main-thread median {Median(s.Main):F2}  render-thread median {Median(s.Render):F2}  draws {s.Draws / n:F0}  batches {s.Batches / n:F0}  setpass {s.SetPass / n:F0}  shadow casters {s.Casters / n:F0}  tris {s.Tris / n / 1000:F0}k";
            }
            _abWrite?.Invoke($"=== Merge A/B {DateTime.Now:HH:mm:ss} ({Stats}), interleaved every 8 frames, camera at {(Camera.main != null ? Camera.main.transform.position.ToString() : "?")}, moved in {Plugin.MovedFrames} frames, window {(Application.isFocused ? "focused" : "in background")}\n{Line("original", _ab[0])}\n{Line("merged", _ab[1])}\n  merged − original: mean {Avg(_ab[1].Dt) - Avg(_ab[0].Dt):+0.00;-0.00} ms, median {Median(_ab[1].Dt) - Median(_ab[0].Dt):+0.00;-0.00} ms, main thread {Median(_ab[1].Main) - Median(_ab[0].Main):+0.00;-0.00} ms, render thread {Median(_ab[1].Render) - Median(_ab[0].Render):+0.00;-0.00} ms");
        }

        private static double Avg(List<double> l) => l.Count == 0 ? 0 : l.Average();

        private static double Median(List<double> l)
        {
            if (l.Count == 0) return 0;
            var s = l.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        public static List<Bench.Experiment> Experiments() => new List<Bench.Experiment>
        {
            new Bench.Experiment("merged statics shown", () => Show(true), () => Show(false)),
        };

        // ---------- keeping in sync with the game ----------

        private static void Check()
        {
            // Every frame: follow floor streaming (it hides/shows the originals) for each group's first piece.
            for (int i = Groups.Count - 1; i >= 0; i--)
            {
                var g = Groups[i];
                var p0 = g.Parts[0].R;
                if (p0 == null || g.Mr == null) { Dissolve(i); continue; }
                bool hidden = p0.forceRenderingOff || !p0.gameObject.activeInHierarchy;
                if (g.Mr.forceRenderingOff != hidden) g.Mr.forceRenderingOff = hidden;
            }
            // A slice per frame: anything that moved, changed material, got re-enabled by the game or disagrees with
            // the first piece's visibility goes back to being its own renderer.
            if (Groups.Count == 0) return;
            int n = Math.Max(1, Groups.Count / 30);
            for (int k = 0; k < n && Groups.Count > 0; k++)
            {
                if (_checkCursor >= Groups.Count) _checkCursor = 0;
                var g = Groups[_checkCursor];
                if (!Intact(g)) Dissolve(_checkCursor, true);
                else _checkCursor++;
            }
        }

        private static bool Intact(Group g)
        {
            var p0 = g.Parts[0].R;
            if (p0 == null) return false;
            bool off0 = p0.forceRenderingOff, act0 = p0.gameObject.activeInHierarchy;
            var mat0 = p0.sharedMaterial; // sharedMaterials would allocate an array per piece
            foreach (var p in g.Parts)
            {
                var r = p.R;
                if (r == null) return false;
                if (_shown && r.enabled) return false; // the game switched it back on
                if (r.forceRenderingOff != off0 || r.gameObject.activeInHierarchy != act0) return false;
                var m = r.transform.localToWorldMatrix;
                for (int i = 0; i < 16; i++) if (Math.Abs(m[i] - p.Matrix[i]) > 1e-4f) return false;
                if (r.sharedMaterial != mat0) return false;
            }
            return true;
        }

        private static void Dissolve(int index, bool ban = false)
        {
            var g = Groups[index];
            Groups.RemoveAt(index);
            foreach (var p in g.Parts)
            {
                if (p.R == null) continue;
                Handled.Remove(p.R);
                if (ban) Banned.Add(p.R);
                if (!p.R.enabled) p.R.enabled = true;
            }
            if (g.Go != null) UnityEngine.Object.Destroy(g.Go);
            if (g.Mesh != null) UnityEngine.Object.Destroy(g.Mesh);
            Merged -= g.Parts.Length;
            Dissolved += g.Parts.Length;
        }

        // A hot reload keeps the previous build of this mod loaded; make sure its merged meshes don't linger.
        private static bool _undidOld;
        private static void UndoOlderCopies()
        {
            if (_undidOld) return;
            _undidOld = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (asm == typeof(StaticMerge).Assembly || !(n == "Performance" || n.StartsWith("Performance-r"))) continue; // the mod manager renames reloads
                try { asm.GetType(typeof(StaticMerge).FullName)?.GetMethod(nameof(DissolveAll))?.Invoke(null, null); }
                catch (Exception e) { Plugin.Log.LogWarning("Old merge cleanup: " + e.Message); }
            }
        }

        public static void DissolveAll()
        {
            for (int i = Groups.Count - 1; i >= 0; i--) Dissolve(i);
            _pending = null;
            Cache.Clear();
            Handled.Clear();
            Banned.Clear();
            Merged = Failed = Dissolved = SkipProbe = SkipLights = 0;
            BuildMs = 0;
            if (_holder != null) UnityEngine.Object.Destroy(_holder);
            _holder = null;
        }
    }
}
