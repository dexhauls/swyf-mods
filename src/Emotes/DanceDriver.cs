using System.Collections.Generic;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Plays Mixamo dances on characters. The game's emote system runs a stand-in clip (so the dance is networked,
    /// cancels on movement and the procedural arm/head animation steps aside); after the Animator has written its
    /// pose, this overwrites the skeleton with the dance frame.
    /// </summary>
    internal sealed class DanceDriver : MonoBehaviour
    {
        private sealed class Active
        {
            public PlayerEmoterNetworking Emoter;
            public Dance Dance;
            public float Start;
            public AudioSource Music;
            public readonly List<Rig> Rigs = new List<Rig>();
        }

        private sealed class Rig
        {
            public Transform[] Bones; // index matches Dance.Bones, null = bone missing on this character
            public Quaternion[] Rest;  // pose before the dance, put back afterwards for bones the Animator leaves alone
            public Transform Hips;
            public Vector3 HipsStart;
            public float Scale;
            public Transform[] Feet;   // toes and ankles: the dance never pushes them below where they stood
            public Transform Body;     // the player object; ground height is kept relative to it (walkable dances)
            public Transform Root;     // the character model (it turns where the player walks; the player object doesn't)
            public float GroundY;      // lowest foot height when the dance began, relative to Body
            public FootPlanting Feet2; // keeps feet planted while walking in a walkable dance
            public float HipsHeight;   // hips above the lowest foot when the dance began (sets the walking speed)
            public GaitTrack Gait;     // what the dance does with its feet (steps, contacts, pace)
            public float Frame;        // dance frame shown this frame
            public DanceTuning Tuning = DanceTuning.Default;
            public RollerSkates Skates;
            public Vector3 LastFacing, LastBody;   // skating: for the lean into turns and the wheels' roll
            public float Lean;
            public bool HasLast;
        }

        private static DanceDriver _instance;
        private readonly List<Active> _active = new List<Active>();

        public static void Begin(PlayerEmoterNetworking emoter, Dance dance)
        {
            if (_instance == null || emoter == null || dance == null) return;
            foreach (var old in _instance._active)
                if (old.Emoter == emoter) Finish(old);
            _instance._active.RemoveAll(a => a.Emoter == emoter);
            var act = new Active { Emoter = emoter, Dance = dance, Start = Time.time };
            foreach (var anim in emoter.anims)
                if (anim != null) act.Rigs.Add(BuildRig(anim.transform, dance, emoter.transform));
            if (emoter.isLocalPlayer)
                foreach (var r in act.Rigs)
                    if (r.Feet2 != null && r.Hips != null && r.Hips.gameObject.activeInHierarchy) { r.Feet2.Diagnose = true; break; }
            // In time with the music: a dance with its own intro starts with the music's intro; one without starts its
            // loop with the music's loop, playing the end of its loop (which leads into it) under the music's intro.
            act.Music = DanceMusic.Start(_instance, emoter.transform, dance,
                (introAt, loopAt) => act.Start = dance.LoopStart > 0 ? introAt : loopAt);
            if (emoter.isLocalPlayer) Survey(act);
            _instance._active.Add(act);
        }

        private static Rig BuildRig(Transform root, Dance dance, Transform body)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("mixamorig:") && !byName.ContainsKey(t.name)) byName[t.name] = t;
            var rig = new Rig { Bones = new Transform[dance.Bones.Length], Rest = new Quaternion[dance.Bones.Length] };
            for (int i = 0; i < dance.Bones.Length; i++)
                if (byName.TryGetValue(dance.Bones[i], out rig.Bones[i])) rig.Rest[i] = rig.Bones[i].localRotation;
            byName.TryGetValue("mixamorig:Hips", out rig.Hips);
            if (rig.Hips != null) rig.HipsStart = rig.Hips.localPosition;
            // Hips movement is in Mixamo metres; scale it to this character by leg length.
            rig.Scale = 1f;
            if (byName.TryGetValue("mixamorig:LeftLeg", out var leg) && dance.LegLength > 0.001f)
                rig.Scale = leg.localPosition.magnitude / dance.LegLength;
            var feet = new List<Transform>();
            foreach (var n in new[] { "mixamorig:LeftToeBase", "mixamorig:RightToeBase", "mixamorig:LeftFoot", "mixamorig:RightFoot" })
                if (byName.TryGetValue(n, out var f)) feet.Add(f);
            rig.Feet = feet.ToArray();
            rig.Body = body;
            rig.Root = root;
            rig.GroundY = LowestFoot(rig) - body.position.y;
            rig.Tuning = DanceTuning.For(dance);
            rig.Feet2 = new FootPlanting(body, root)
            {
                StepArc = rig.Tuning.StepArc, FallbackGap = rig.Tuning.FallbackGap, SwaySteps = rig.Tuning.SwaySteps,
                SlideAsDanced = rig.Tuning.SlideAsDanced, NoFootIK = rig.Tuning.NoFootIK,
                SlideWalk = rig.Tuning.SlideWalk, Stride = rig.Tuning.Stride, WalkLift = rig.Tuning.WalkLift,
                Label = dance.Label,
            };
            if (rig.Hips != null) rig.HipsHeight = rig.Hips.position.y - (body.position.y + rig.GroundY);
            if (rig.Tuning.Skates && rig.Hips != null && rig.Hips.gameObject.activeInHierarchy)
                rig.Skates = RollerSkates.Create(root, body, rig.Feet2.LegLength);
            if (rig.Hips != null && MovableDances.IsMovable(dance.Id)) rig.Gait = GaitTrack.Build(dance, rig.Hips, rig.Scale, root);
            return rig;
        }

        private static float LowestFoot(Rig rig)
        {
            float y = float.MaxValue;
            foreach (var f in rig.Feet) y = Mathf.Min(y, f.position.y);
            return y;
        }

        /// <summary>
        /// The game's characters have shorter legs than Mixamo's, so some poses would sink into the floor: lift the
        /// hips until the lowest foot is back at the height it stood at when the dance began.
        /// </summary>
        private static void KeepAboveFloor(Rig rig)
        {
            if (rig.Hips == null || rig.Body == null || rig.Feet.Length == 0 || rig.Hips.parent == null) return;
            float sink = rig.GroundY + (rig.Body != null ? rig.Body.position.y : 0f) - LowestFoot(rig);
            if (sink <= 0.0005f) return;
            rig.Hips.localPosition += rig.Hips.parent.InverseTransformVector(new Vector3(0, sink, 0));
        }

        private static readonly List<RollerSkates> Leaving = new List<RollerSkates>();   // skates shrinking away

        private static void Finish(Active a)
        {
            foreach (var rig in a.Rigs)
                if (rig.Skates != null) { rig.Skates.Hide(); Leaving.Add(rig.Skates); rig.Skates = null; }
            DanceMusic.Forget(a.Music);
            if (a.Music != null) Destroy(a.Music.gameObject);
            a.Music = null;
            foreach (var rig in a.Rigs)
            {
                for (int b = 0; b < rig.Bones.Length; b++)
                    if (rig.Bones[b] != null) rig.Bones[b].localRotation = rig.Rest[b];
                if (rig.Hips != null) rig.Hips.localPosition = rig.HipsStart;
            }
        }

        // Once per session: how every walkable dance steps, on this character (for tuning).
        private static bool _surveyed;  // reset by a reload (new assembly)
        private static void Survey(Active act)
        {
            if (_surveyed) return;
            Rig rig = null;
            foreach (var r in act.Rigs) if (r.Hips != null && r.Hips.gameObject.activeInHierarchy && r.Feet2 != null) rig = r;
            if (rig == null) return;
            _surveyed = true;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("WALKABLE DANCES (steps per loop, loop length, pace min/avg/max in hip-heights per s, longest still part):\n");
            float hip = Mathf.Max(0.2f, rig.HipsHeight), leg = rig.Feet2.LegLength;
            foreach (var d in DanceLibrary.All)
            {
                if (!MovableDances.IsMovable(d.Id)) continue;
                var g = GaitTrack.Build(d, rig.Hips, rig.Scale, rig.Root);
                if (g == null) { sb.AppendLine($"  {d.Label,-22} analysis failed"); continue; }
                float min = float.MaxValue, max = 0f, sum = 0f; int n = 0;
                for (int f = g.LoopStart; f < g.Frames; f++) { float v = g.Pace[f] * leg / hip; min = Mathf.Min(min, v); max = Mathf.Max(max, v); sum += v; n++; }
                sb.AppendLine(string.Format(inv, "  {0,-22} steps {1,3}  loop {2,5:0.0}s  pace {3:0.00}/{4:0.00}/{5:0.00}  still {6:0.0}s  facing {7:+0.00;-0.00}",
                    d.Label, g.StepsPerLoop, (g.Frames - g.LoopStart) / g.Fps, min, sum / Mathf.Max(1, n), max, g.LongestStill / g.Fps, g.FacingDot));
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Walking pace this dance allows right now (world units/s), from how long its feet stand (see GaitTrack.Pace).
        /// 0 = unknown (no analysis).
        /// </summary>
        public static float GaitSpeed(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return 0f;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null && rig.Hips != null && rig.Hips.gameObject.activeInHierarchy && rig.Feet2 != null)
                        {
                            int f = Mathf.Clamp((int)rig.Frame, 0, rig.Gait.Frames - 1), f1 = Mathf.Min(f + 1, rig.Gait.Frames - 1);
                            LastInPlace = rig.Feet2.InPlaceMode;
                            var p = rig.Feet2.InPlaceMode ? rig.Gait.PaceInPlace : rig.Gait.Pace;
                            return Mathf.Lerp(p[f], p[f1], rig.Frame - (int)rig.Frame) * rig.Feet2.LegLength;
                        }
            return 0f;
        }

        /// <summary>True while the dancer walks forward in a dance whose speed comes straight from its pushes.</summary>
        public static bool FollowsDance(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return false;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null && rig.Gait.FollowDance && rig.Feet2 != null && rig.Hips != null && rig.Hips.gameObject.activeInHierarchy)
                            return !rig.Feet2.InPlaceMode;
            return false;
        }

        /// <summary>
        /// For the camera: how much the local player is walking in their dance (0..1) and where its music is in the
        /// beat (false when there's no music or tempo).
        /// </summary>
        public static bool LocalBeat(out float walk, out float phase)
        {
            walk = 0f;
            phase = 0f;
            if (_instance == null) return false;
            foreach (var a in _instance._active)
            {
                if (a.Emoter == null || !a.Emoter.isLocalPlayer) continue;
                foreach (var rig in a.Rigs)
                    if (rig.Feet2 != null && rig.Hips != null && rig.Hips.gameObject.activeInHierarchy) walk = rig.Feet2.Weight;
                return DanceMusic.BeatPhase(a.Music, out phase);
            }
            return false;
        }

        /// <summary>How quickly the local dancer's speed may change (DanceTuning.Momentum); 0 = at once.</summary>
        public static float Momentum(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return 0f;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null) return rig.Tuning.Momentum;
            return 0f;
        }

        // Skating (and DanceTuning.LeanIntoTurns): lean into turns like on real skates (from the floor, by how hard the turn is at this speed) and roll
        // the wheels by how far the skater went.
        private static void Skate(Rig rig, float dt)
        {
            if (rig.Root == null || rig.Body == null || rig.Hips == null || dt <= 0f) return;
            Vector3 facing = rig.Root.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) return;
            facing.Normalize();
            Vector3 body = rig.Body.position;
            if (!rig.HasLast) { rig.LastFacing = facing; rig.LastBody = body; rig.HasLast = true; }
            Vector3 moved = body - rig.LastBody;
            moved.y = 0f;
            float speed = moved.magnitude / dt;
            float turn = Vector3.SignedAngle(rig.LastFacing, facing, Vector3.up) * 0.0174533f / dt;   // rad/s, + = right
            rig.LastFacing = facing;
            rig.LastBody = body;
            // the angle that balances the turn (tan = sideways pull / gravity), a little less, at most 22 degrees
            float lean = Mathf.Clamp(-Mathf.Atan(speed * turn / 9.81f) * 57.29578f * 0.8f, -22f, 22f);
            rig.Lean = Mathf.Lerp(rig.Lean, lean, Mathf.Clamp01(dt * 6f));
            var r = Quaternion.AngleAxis(rig.Lean, facing);
            rig.Hips.position = body + r * (rig.Hips.position - body);
            rig.Hips.rotation = r * rig.Hips.rotation;
            rig.Skates?.Roll(Vector3.Dot(moved, facing));
        }

        /// <summary>
        /// The local player is skating (a dance with skates): where the skates point and how hard the dance pushes right
        /// now (0..1).
        /// </summary>
        public static bool LocalSkating(out Vector3 facing, out float push)
        {
            facing = Vector3.forward;
            push = 0f;
            if (_instance == null) return false;
            foreach (var a in _instance._active)
            {
                if (a.Emoter == null || !a.Emoter.isLocalPlayer) continue;
                foreach (var rig in a.Rigs)
                {
                    if (rig.Skates == null || rig.Root == null) continue;
                    facing = rig.Root.forward;
                    facing.y = 0f;
                    facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
                    if (rig.Gait != null)
                    {
                        float max = 0.01f;
                        foreach (var p in rig.Gait.Pace) max = Mathf.Max(max, p);
                        int f = Mathf.Clamp((int)rig.Frame, 0, rig.Gait.Frames - 1);
                        push = Mathf.Clamp01(rig.Gait.Pace[f] / max);
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>True while the dancer's dance is walked by hops (DanceTuning.HopPush): its speed springs with each hop.</summary>
        public static bool HopPaced(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return false;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null && rig.Feet2 != null && rig.Tuning.HopPush > 0f) return !rig.Feet2.InPlaceMode;
            return false;
        }

        /// <summary>A fixed walking speed for the dancer's dance (DanceTuning.WalkSpeed, world units/s); 0 = none.</summary>
        public static float FixedWalkSpeed(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return 0f;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null) return rig.Tuning.WalkSpeed;
            return 0f;
        }

        /// <summary>Whether the last <see cref="GaitSpeed"/> was for walking sideways/backwards (for the log).</summary>
        public static bool LastInPlace;

        /// <summary>True when the dancer's current dance has a foot analysis (its pace may then be 0).</summary>
        public static bool HasGait(PlayerEmoterNetworking emoter)
        {
            if (_instance == null) return false;
            foreach (var a in _instance._active)
                if (a.Emoter == emoter)
                    foreach (var rig in a.Rigs)
                        if (rig.Gait != null) return true;
            return false;
        }

        /// <summary>Height of the dancer's hips above their feet (world units) when the dance began; 1 if unknown.</summary>
        public static float HipsHeight(PlayerEmoterNetworking emoter)
        {
            if (_instance != null)
                foreach (var a in _instance._active)
                    if (a.Emoter == emoter)
                        foreach (var rig in a.Rigs)
                            if (rig.Hips != null && rig.Hips.gameObject.activeInHierarchy && rig.Body != null)
                                return Mathf.Max(0.2f, rig.HipsHeight);
            return 1f;
        }

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            foreach (var a in _active) Finish(a);
            _active.Clear();
            foreach (var s in Leaving) s.Destroy();
            Leaving.Clear();
            EmoteAnchors.RestoreAll();
            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i];
                var d = a.Dance;
                float t = Time.time - a.Start;
                if (a.Emoter == null || !a.Emoter.IsEmoting || (t >= d.Length && !d.Loop)) { Finish(a); _active.RemoveAt(i); continue; }
                // The intro (frames before LoopStart) plays once, then the rest repeats.
                float frame = t * d.Fps;
                int loopLen = d.Frames - d.LoopStart;
                if (frame >= d.Frames - 1 && loopLen > 1) frame = d.LoopStart + (frame - d.LoopStart) % (loopLen - 1);
                else if (frame < 0f)
                    frame = d.LoopStart > 0 || loopLen <= 1 ? 0f : Mathf.Repeat(frame, loopLen - 1);   // under the music's intro
                int f0 = Mathf.Min((int)frame, d.Frames - 1), f1 = Mathf.Min(f0 + 1, d.Frames - 1);
                float k = frame - f0;
                foreach (var rig in a.Rigs)
                {
                    for (int b = 0; b < rig.Bones.Length; b++)
                    {
                        var bone = rig.Bones[b];
                        if (bone == null) continue;
                        var q = d.Rotations[b];
                        bone.localRotation = Quaternion.Slerp(q[f0], q[f1], k);
                    }
                    if (rig.Hips != null)
                    {
                        var offset = Vector3.Lerp(d.HipsOffset[f0], d.HipsOffset[f1], k) * rig.Scale;
                        // Walking in the dance: the body travels with the player, so keep the dance's own hip
                        // travel small (by default side/forward to ~10 %, bounce to a quarter; see DanceTuning).
                        float w = rig.Feet2 != null ? rig.Feet2.Weight : 0f;
                        float sway = 1f - (1f - rig.Tuning.HipSway) * w, bounce = 1f - (1f - rig.Tuning.HipBounce) * w;
                        float side = rig.Tuning.HipSide >= 0f ? 1f - (1f - rig.Tuning.HipSide) * w : sway;
                        offset.x *= side;
                        offset.z *= sway;
                        offset.y *= bounce;
                        rig.Hips.localPosition = rig.HipsStart + offset;
                    }
                    KeepAboveFloor(rig);
                    // Up onto the skates' wheels as they pop out.
                    if (rig.Skates != null && rig.Hips != null) rig.Hips.position += Vector3.up * rig.Skates.Tick(Time.deltaTime);
                    rig.Frame = frame;
                    // Leaning before the legs are fitted keeps planted feet where they stand (skates roll, they lean after).
                    if (rig.Skates == null && rig.Tuning.LeanIntoTurns) Skate(rig, Time.deltaTime);
                    rig.Feet2?.Apply(MovableDances.Active(a.Emoter), Time.deltaTime, rig.Gait, frame);
                    if (rig.Skates != null) Skate(rig, Time.deltaTime);
                    rig.Skates?.Follow();
                }

                if (a.Music != null)
                {
                    bool duck = DanceMusic.SomeoneTalkingNear(a.Music.transform.position);
                    float target = duck ? DanceMusic.DuckedVolume : DanceMusic.Volume;
                    float v = Mathf.MoveTowards(a.Music.volume, target, Time.unscaledDeltaTime * (duck ? 2.5f : 0.6f));
                    foreach (var src in a.Music.GetComponents<AudioSource>()) src.volume = v;
                }
            }
            for (int i = Leaving.Count - 1; i >= 0; i--)
            {
                Leaving[i].Tick(Time.deltaTime);
                if (Leaving[i].Gone) Leaving.RemoveAt(i);
                else Leaving[i].Follow();
            }
            EmoteAnchors.LateTick();
        }
    }
}
