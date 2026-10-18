using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Roller skates for Roller Vibes (DanceTuning.Skates): the model from tools/skate_model.py (Blender, saved as
    /// skate.bin next to the mod) fitted over the dancer's shoes - the boot a bit bigger than the shoe so it hides it,
    /// the wheels under the sole. The shoe is measured from the character's mesh. They pop up when the dance starts
    /// (with a little springy overshoot) while the dancer rises onto the wheels and the shoes vanish into them (the
    /// foot bones shrink to nothing, so the shoe folds into the boot at the ankle), and shrink away when it ends.
    /// The skates follow the feet each frame rather than hang from them (the shrunk bones would shrink them too).
    /// Everyone sees them: every player's game runs the dance.
    /// </summary>
    internal sealed class RollerSkates
    {
        private const float PopTime = 0.45f, HideTime = 0.2f;

        // The model's nominal size (tools/skate_model.py): 1 long, boot 0.42 wide and 0.62 high, wheels 0.29 deep.
        private const float ModelWidth = 0.42f, ModelBootHeight = 0.62f, ModelLift = 0.05f + 0.24f, ModelWheelRadius = 0.12f;

        private static Shader _shader, _fallback;
        private static Dictionary<string, List<(Mesh mesh, Material mat)>> _model;
        private static bool _modelTried;

        private sealed class Strap
        {
            public Transform Skate, Foot;
            public Vector3 Pos, FootScale;   // skate relative to the foot (unscaled), the foot's own scale
            public Quaternion Rot;
        }

        private readonly List<Strap> _skates = new List<Strap>();
        private float _shoesGone;   // 0..1: how far the shoes have folded away
        private readonly List<Transform> _wheels = new List<Transform>();
        private float _wheelRadius = 0.05f, _wheelTurn;

        /// <summary>Rolls the wheels by how far the skater moved forwards (world units, negative = backwards).</summary>
        public void Roll(float distance)
        {
            _wheelTurn = Mathf.Repeat(_wheelTurn + distance / Mathf.Max(0.01f, _wheelRadius) * 57.29578f, 360f);   // (Mathf.Rad2Deg is stripped from this game)
            foreach (var w in _wheels)
                if (w != null) w.localRotation = Quaternion.Euler(_wheelTurn, 0f, 0f);
        }
        private float _time;
        private bool _hiding;

        /// <summary>How far the wheels lift the dancer (world units) once they are fully out.</summary>
        public float Height { get; private set; }

        public bool Gone => _skates.Count == 0;

        /// <summary>The shoe's footprint as it stands now, in a frame along the foot.</summary>
        private struct Shoe
        {
            public Vector3 Centre, Forward;   // centre of the footprint, at the sole
            public float Length, Width, AnkleHeight;
        }

        public static RollerSkates Create(Transform root, Transform body, float legLength)
        {
            var skates = new RollerSkates();
            if (!LoadModel()) return skates;
            var bones = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("mixamorig:") && !bones.ContainsKey(t.name)) bones[t.name] = t;
            var shoes = new List<(string side, Transform foot, Shoe shoe)>();
            foreach (var side in new[] { "Left", "Right" })
                if (bones.TryGetValue("mixamorig:" + side + "Foot", out var foot) && bones.TryGetValue("mixamorig:" + side + "ToeBase", out var toe))
                {
                    bones.TryGetValue("mixamorig:" + side + "Leg", out var knee);
                    bones.TryGetValue("mixamorig:" + (side == "Left" ? "Right" : "Left") + "Foot", out var other);
                    shoes.Add((side, foot, Measure(root, foot, toe, knee, other, body.position.y)));
                }
            if (shoes.Count == 0) return skates;
            // Both the same size (one foot can be measured oddly when the legs are crossed), a little bigger than the
            // shoes so the boots hide them, heels included.
            float len = 0f, width = 0f, ankleUp = 0f;
            foreach (var s in shoes)
            {
                len += s.shoe.Length * 1.18f / shoes.Count;
                width += s.shoe.Width * 1.22f / shoes.Count;
                ankleUp += s.shoe.AnkleHeight / shoes.Count;
            }
            skates.Height = len * ModelLift;
            foreach (var (side, foot, shoe) in shoes) skates.Build(side, foot, shoe, len, width, ankleUp);
            var a = shoes[0].shoe;
            Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Skates: shoe {0:0.000} long, {1:0.000} wide, ankle {2:0.000} up, sole {3:+0.000;-0.000} from the player, wheels lift {4:0.000}",
                a.Length, a.Width, a.AnkleHeight, a.Centre.y - body.position.y, skates.Height));
            return skates;
        }

        // The shoe from the character's mesh: the vertices low down by this foot (nearer it than the other foot).
        // Its footprint is taken from the lowest part only, so trouser legs hanging over it don't count.
        private static Shoe Measure(Transform root, Transform foot, Transform toe, Transform knee, Transform otherFoot, float floor)
        {
            Vector3 ankle = foot.position;
            Vector3 fwd = toe.position - ankle;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : root.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float shin = knee != null ? Mathf.Max(0.05f, knee.position.y - ankle.y) : 0.3f;
            float maxHeight = ankle.y + shin * 0.2f, reach = shin * 0.9f;
            Vector3 a0 = Flat(ankle), a1 = Flat(toe.position), o0 = otherFoot != null ? Flat(otherFoot.position) : a0 + Vector3.one * 99f;
            var pts = new List<Vector3>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked, true);
                    var tr = smr.transform;
                    foreach (var v in baked.vertices)
                    {
                        Vector3 p = tr.position + tr.rotation * v;
                        if (p.y > maxHeight) continue;
                        Vector3 f = Flat(p);
                        float mine = DistToSegment(f, a0, a1);
                        if (mine > reach || mine > Vector3.Distance(f, o0)) continue;
                        pts.Add(p);
                    }
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"Skates: could not read {smr.name}: {e.Message}"); }
                finally { Object.Destroy(baked); }
            }
            float minY = float.MaxValue;
            foreach (var p in pts) minY = Mathf.Min(minY, p.y);
            float sole = minY + Mathf.Max(0.01f, (ankle.y - minY) * 0.45f);
            float back = float.MaxValue, front = float.MinValue, left = float.MaxValue, rightMost = float.MinValue;
            int found = 0;
            foreach (var p in pts)
            {
                if (p.y > sole) continue;
                Vector3 d = p - ankle;
                float along = Vector3.Dot(d, fwd), side = Vector3.Dot(d, right);
                back = Mathf.Min(back, along); front = Mathf.Max(front, along);
                left = Mathf.Min(left, side); rightMost = Mathf.Max(rightMost, side);
                found++;
            }
            if (found < 20)
            {
                float a = Mathf.Max(0.04f, ankle.y - floor), toeLen = Vector3.Distance(ankle, toe.position);
                minY = floor;
                back = -a * 1.2f; front = toeLen + a * 1.6f; left = -a * 1.1f; rightMost = a * 1.1f;
            }
            return new Shoe
            {
                Forward = fwd,
                Centre = new Vector3(ankle.x, minY, ankle.z) + fwd * ((front + back) * 0.5f) + right * ((rightMost + left) * 0.5f),
                Length = front - back, Width = rightMost - left, AnkleHeight = ankle.y - minY,
            };
        }

        // Built over the shoe where the foot stands now and strapped to the foot; the dancer then rises onto the wheels.
        private void Build(string side, Transform foot, Shoe shoe, float len, float width, float ankleUp)
        {
            var skate = new GameObject("Emoty skate " + side).transform;
            skate.position = shoe.Centre - shoe.Forward * (len * 0.19f);   // back, over the heel (the shoe sits forward of its footprint)
            skate.rotation = Quaternion.LookRotation(shoe.Forward, Vector3.up);
            float wide = width / ModelWidth;
            float bootHeight = Mathf.Max(ankleUp * 2f, len * ModelBootHeight * 0.75f);
            _wheelRadius = ModelWheelRadius * len;
            var boot = new GameObject("boot").transform;
            boot.SetParent(skate, false);
            boot.localScale = new Vector3(wide, bootHeight / ModelBootHeight, len);
            var chassis = new GameObject("chassis").transform;
            chassis.SetParent(skate, false);
            chassis.localScale = new Vector3(Mathf.Max(wide, len * 0.8f), len, len);   // even in height and length: round wheels
            foreach (var kv in _model)
            {
                Transform holder;
                if (kv.Key == "boot") holder = boot;
                else if (kv.Key.StartsWith("wheel"))
                {
                    // each wheel turns about its own axle
                    var b = kv.Value[0].mesh.bounds;
                    foreach (var piece in kv.Value) b.Encapsulate(piece.mesh.bounds);
                    holder = new GameObject(kv.Key).transform;
                    holder.SetParent(chassis, false);
                    holder.localPosition = b.center;
                    _wheels.Add(holder);
                }
                else holder = chassis;
                foreach (var (mesh, mat) in kv.Value)
                {
                    var go = new GameObject(kv.Key + " " + mesh.name);
                    go.transform.SetParent(holder, false);
                    if (kv.Key.StartsWith("wheel")) go.transform.localPosition = -holder.localPosition;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    if (mat != null) r.sharedMaterial = mat;
                }
            }
            var inv = Quaternion.Inverse(foot.rotation);
            _skates.Add(new Strap
            {
                Skate = skate, Foot = foot, FootScale = foot.localScale,
                Pos = inv * (skate.position - foot.position), Rot = inv * skate.rotation,
            });
            skate.localScale = Vector3.zero;
        }

        /// <summary>After the dance pose (and any leg bending) of this frame: skates onto the feet, shoes folded away.</summary>
        public void Follow()
        {
            foreach (var s in _skates)
            {
                if (s.Skate == null || s.Foot == null) continue;
                s.Skate.SetPositionAndRotation(s.Foot.position + s.Foot.rotation * s.Pos, s.Foot.rotation * s.Rot);
                s.Foot.localScale = s.FootScale * Mathf.Lerp(1f, 0.02f, _shoesGone);
            }
        }

        private static bool LoadModel()
        {
            if (_model != null) return true;
            if (_modelTried) return false;
            _modelTried = true;
            string path = Path.Combine(BepInEx.Paths.GameRootPath, "Mods", "Emotes", "skate.bin");
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "SWYK") throw new InvalidDataException("not a skate file");
                    r.ReadUInt32();
                    var model = new Dictionary<string, List<(Mesh, Material)>>();
                    uint parts = r.ReadUInt32();
                    for (int p = 0; p < parts; p++)
                    {
                        string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                        var list = new List<(Mesh, Material)>();
                        uint mats = r.ReadUInt32();
                        for (int m = 0; m < mats; m++)
                        {
                            var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            float smoothness = r.ReadSingle();
                            int nv = (int)r.ReadUInt32();
                            var verts = new Vector3[nv];
                            var normals = new Vector3[nv];
                            for (int i = 0; i < nv; i++)
                            {
                                verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            }
                            int ni = (int)r.ReadUInt32();
                            var idx = new int[ni];
                            for (int i = 0; i < ni; i++) idx[i] = (int)r.ReadUInt32();
                            var mesh = new Mesh { name = name + m };
                            if (nv > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                            mesh.vertices = verts;
                            mesh.normals = normals;
                            mesh.triangles = idx;
                            mesh.RecalculateBounds();
                            list.Add((mesh, MaterialFor(color, smoothness)));
                        }
                        model[name] = list;
                    }
                    _model = model;
                }
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Skates: could not load {path}: {e.Message}");
                return false;
            }
        }

        private static Material MaterialFor(Color color, float smoothness)
        {
            if (_shader == null)
            {
                foreach (var name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit" })
                    if ((_shader = Shader.Find(name)) != null) break;
                Plugin.Log.LogInfo($"Skates shader: {(_shader != null ? _shader.name : "none")}");
            }
            if (_shader == null) return null;
            var m = new Material(_shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>Moves the pop / shrink along; returns how far to lift the dancer right now (world units).</summary>
        public float Tick(float dt)
        {
            _time += dt;
            float s;
            if (_hiding)
            {
                float k = Mathf.Clamp01(_time / HideTime);
                s = 1f - k * k;
                if (k >= 1f) { Destroy(); return 0f; }
            }
            else
            {
                // ease out with a springy overshoot
                float x = Mathf.Clamp01(_time / PopTime) - 1f;
                s = 1f + 2.70158f * x * x * x + 1.70158f * x * x;
            }
            foreach (var strap in _skates)
                if (strap.Skate != null) strap.Skate.localScale = Vector3.one * Mathf.Max(0f, s);
            float rise = Mathf.Clamp01(_time / PopTime);
            _shoesGone = _hiding ? 1f - Mathf.Clamp01(_time / HideTime) : Mathf.Clamp01(_time / (PopTime * 0.6f));
            return _hiding ? 0f : Height * rise * rise * (3f - 2f * rise);
        }

        public void Hide()
        {
            if (_hiding) return;
            _hiding = true;
            _time = 0f;
        }

        public void Destroy()
        {
            foreach (var s in _skates)
            {
                if (s.Foot != null) s.Foot.localScale = s.FootScale;   // shoes back
                if (s.Skate != null) Object.Destroy(s.Skate.gameObject);
            }
            _skates.Clear();
        }
    }
}
