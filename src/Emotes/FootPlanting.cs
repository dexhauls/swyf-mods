using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Walking in a dance without skating. Steps follow the dance itself (see <see cref="GaitTrack"/>):
    /// while the dance keeps a foot on the ground it stays pinned to its spot and the leg bends (two-bone IK);
    /// when the dance lifts the foot it travels with the dance's own lift and lands a bit ahead in the walking
    /// direction, so it then stands under the body for the rest of its stance. Feet the dance never lifts get a
    /// quick fallback step when they fall too far behind. Standing still the dance plays untouched.
    /// </summary>
    internal sealed class FootPlanting
    {
        private sealed class Leg
        {
            public Transform Up, Knee, Foot;
            public float UpperLen, LowerLen;
            public float AnkleRest;          // ankle height above the player's feet when the dance began
            public bool Planted, Stepping, InAir;
            public Vector3 Pin, From, To;
            public float T, PlantedFor, AirFor;
            public Vector3 LiftOffset;       // pin minus the animated foot when the dance lifted it
            public Vector3 LandOffset;       // where it will stand relative to the animated foot (set while in the air)
            public Vector3 LastRel;          // animated foot relative to the player last frame (FollowDance)
            public bool HasLastRel;
            public Vector3 StepAnim, StepBody; // own step: the animated foot and the player when it began
            public float AirU0;              // how far into the dance's lift the foot joined it (0 = from the start)
            public float Lead;               // how far ahead of the dance it may stand (what it landed ahead, shrinking)
            public Vector3 LastTarget;       // for the report
            public bool HasLastTarget;
            public Vector3 LastAnim;
            public string LastState = "", Why = "";
        }

        private const float FallbackStepTime = 0.3f;
        private readonly Leg[] _legs;
        private readonly Transform _body, _hips;
        private float _hipDrop;
        private Vector3 _lastBody, _velocity;
        private float _weight;              // 0 = pure dance, 1 = walking
        private bool _hasLast;

        /// <summary>True while walking sideways or backwards: the dance's own forward foot slide doesn't apply.</summary>
        public bool InPlaceMode { get; private set; }

        /// <summary>How much the player is walking in the dance right now (0 = standing, 1 = walking).</summary>
        public float Weight => _weight;

        /// <summary>Extra lift on steps, 0..1 (see <see cref="DanceTuning.StepArc"/>).</summary>
        public float StepArc { get; set; } = 1f;

        /// <summary>See <see cref="DanceTuning.FallbackGap"/> and <see cref="DanceTuning.SwaySteps"/>.</summary>
        public float FallbackGap { get; set; } = 0.55f;
        public bool SwaySteps { get; set; }
        public bool SlideAsDanced { get; set; }
        public bool NoFootIK { get; set; }

        /// <summary>See <see cref="DanceTuning.SlideWalk"/> and <see cref="DanceTuning.Stride"/>.</summary>
        public bool SlideWalk { get; set; }
        public float Stride { get; set; } = 0.8f;
        public float WalkLift { get; set; }
        private float _slidePhase;

        public FootPlanting(Transform body, Transform root)
        {
            _body = body;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "mixamorig:Hips") { _hips = t; break; }
            _legs = new[] { MakeLeg(root, "Left"), MakeLeg(root, "Right") };
        }

        private Leg MakeLeg(Transform root, string side)
        {
            Transform up = null, knee = null, foot = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "mixamorig:" + side + "UpLeg") up = t;
                else if (t.name == "mixamorig:" + side + "Leg") knee = t;
                else if (t.name == "mixamorig:" + side + "Foot") foot = t;
            }
            if (up == null || knee == null || foot == null) return null;
            return new Leg
            {
                Up = up, Knee = knee, Foot = foot,
                UpperLen = Vector3.Distance(up.position, knee.position),
                LowerLen = Vector3.Distance(knee.position, foot.position),
                AnkleRest = foot.position.y - _body.position.y,
            };
        }

        public float LegLength => _legs[0] != null ? _legs[0].UpperLen + _legs[0].LowerLen : 0.5f;

        /// <summary>After the dance pose of <paramref name="frame"/> is applied.</summary>
        public void Apply(bool walking, float dt, GaitTrack track, float frame)
        {
            if (_body == null || dt <= 0f) return;
            var pos = _body.position;
            if (_hasLast)
            {
                var v = (pos - _lastBody) / dt;
                v.y = 0f;
                _velocity = Vector3.Lerp(_velocity, v, Mathf.Clamp01(dt * 12f));
            }
            _lastBody = pos;
            _hasLast = true;

            bool moving = walking && _velocity.magnitude > 0.08f;
            _weight = Mathf.MoveTowards(_weight, moving ? 1f : 0f, dt / 0.2f);
            if (_weight <= 0f)
            {
                foreach (var l in _legs) if (l != null) { l.Planted = false; l.Stepping = false; l.InAir = false; l.HasLastRel = false; }
                _hipDrop = 0f;
                return;
            }

            int f = track != null ? Mathf.Clamp((int)frame, 0, track.Frames - 1) : 0;
            float speed = _velocity.magnitude;
            // Where the dancer faces: the player object itself doesn't turn with the character, the model does.
            Vector3 facing = _body.forward;
            if (track != null && _hips != null && _hips.parent != null)
            {
                var w = Flat(_hips.parent.TransformDirection(track.LocalForward));
                if (w.sqrMagnitude > 1e-6f) facing = w.normalized;
            }
            Vector3 dir = speed > 0.01f ? _velocity / speed : facing;
            InPlaceMode = Vector3.Dot(dir, facing) < 0.7f;
            float paceNow = track != null ? (InPlaceMode ? track.PaceInPlace[f] : track.Pace[f]) * LegLength : 0f;
            // Walking slower than the dance allows (or turning): land proportionally less far ahead.
            float ratio = paceNow > 0.02f ? Mathf.Clamp(speed / paceNow, 0f, 1.5f) : 0f;
            if (NoFootIK) { _hipDrop = 0f; return; }   // legs exactly as danced (DanceTuning.NoFootIK)
            // Walking forward in a dance that walks by itself (DanceTuning.FollowDance): legs exactly as danced.
            bool follow = track != null && track.FollowDance && !InPlaceMode;
            var targets = new Vector3[2];
            if (SlideWalk) _slidePhase = Mathf.Repeat(_slidePhase + speed * dt / Mathf.Max(0.05f, Stride * LegLength), 1f);
            for (int i = 0; i < _legs.Length; i++)
            {
                var leg = _legs[i];
                if (leg == null) continue;
                var other = _legs[1 - i];
                Vector3 anim = leg.Foot.position;
                if (SlideWalk)
                {
                    targets[i] = Vector3.Lerp(anim, SlideStep(leg, i, anim, pos, dir), _weight);
                    continue;
                }
                float legLen = leg.UpperLen + leg.LowerLen;
                float floor = pos.y + leg.AnkleRest;
                bool danceOnGround = track != null ? track.Contact[i][f] : anim.y <= floor + legLen * 0.08f;
                float airU = 1f;   // how far through the dance's current lift (0..1)
                if (track != null && !danceOnGround)
                {
                    int start = track.AirStart[i][f], end = track.AirEnd[i][f];
                    airU = Mathf.Clamp01((frame - start) / Mathf.Max(1, end - start));
                }
                // A foot that wasn't up when the dance lifted it (its own step just ended) doesn't jump in at the end
                // of the lift: it stays down if most of the lift is over.
                if (!danceOnGround && !leg.InAir && !leg.Stepping && airU > 0.6f) danceOnGround = true;
                Vector3 target;

                if (leg.Stepping)
                {
                    // Fallback step (for feet the dance doesn't lift often enough).
                    leg.T += dt / FallbackStepTime;
                    float t = Mathf.Clamp01(leg.T);
                    float s = t * t * (3f - 2f * t);
                    // Aim where the dance had the foot when the step began, carried along with the body - not where
                    // it is now, or a swaying dance would swing the foot about mid-step.
                    var aim = leg.StepAnim + (pos - leg.StepBody) + _velocity * FallbackStepTime * 0.5f;
                    leg.To = Ground(aim, floor, anim.y);
                    target = Vector3.Lerp(leg.From, leg.To, s) + Vector3.up * Mathf.Sin(t * 3.14159265f) * legLen * 0.12f;
                    if (t >= 1f) { leg.Stepping = false; leg.Planted = true; leg.PlantedFor = 0f; leg.Pin = leg.To; leg.HasLastRel = false; leg.Lead = Mathf.Max(0f, Vector3.Dot(Flat(leg.Pin - anim), dir)); target = leg.Pin; }
                }
                else if (!danceOnGround)
                {
                    // The dance lifts the foot: keep its lift, carry it from where it stood to ahead of the body.
                    leg.AirFor += dt;
                    if (!leg.InAir)
                    {
                        if (Diagnose) _stats.Steps[i]++;
                        leg.InAir = true;
                        leg.AirFor = 0f;
                        leg.AirU0 = airU < 1f ? airU : 0f;
                        // Where the foot stood, height included: the dance only counts it lifted once it is a bit up,
                        // so the height blends over the lift instead of jumping.
                        leg.LiftOffset = leg.Planted ? leg.Pin - anim : Vector3.zero;
                    }
                    leg.Planted = false;
                    leg.HasLastRel = false;
                    Vector3 landOffset = Vector3.zero;
                    float u = 1f;
                    if (track != null)
                    {
                        // Joined late: travel over what is left of the lift, never jump ahead.
                        u = Mathf.Clamp01((airU - leg.AirU0) / Mathf.Max(0.05f, 1f - leg.AirU0));
                        float ahead = InPlaceMode ? track.LandAheadInPlace[i][f] : track.LandAhead[i][f];
                        // Stands under the body mid-stance. A dance that pushes the body along lands where it's danced
                        // (LandAhead is then ~0 anyway); one walked by steps lands ahead.
                        landOffset = follow && !track.StepAssist ? Vector3.zero : dir * (ahead * LegLength * ratio);
                    }
                    leg.LandOffset = landOffset;
                    float e = u * u * (3f - 2f * u);
                    target = anim + Vector3.Lerp(leg.LiftOffset, landOffset, e);
                    // Dances that barely lift the feet (Fast Feet) would shuffle: lift the foot in proportion to
                    // how far it travels.
                    float travel = Flat(landOffset - leg.LiftOffset).magnitude;
                    target += Vector3.up * Mathf.Min(travel * 0.6f, legLen * 0.15f) * StepArc * Mathf.Sin(u * 3.14159265f);
                    // Touch down where it will stand: over the last third the height eases onto the floor.
                    float settle = Mathf.Clamp01((u - 0.67f) / 0.33f);
                    if (settle > 0f)
                    {
                        // all the way to where it will stand, so touching down doesn't drop it the last bit
                        float groundY = Ground(target, floor, anim.y).y;
                        if (target.y > groundY) target.y = Mathf.Lerp(target.y, groundY, settle * settle * (3f - 2f * settle));
                    }
                }
                else
                {
                    if (!leg.Planted || leg.InAir)
                    {
                        if (Diagnose && leg.InAir && leg.AirFor < 0.1f) _stats.Flicker++;
                        leg.Planted = true;
                        leg.PlantedFor = 0f;
                        leg.Pin = Ground(leg.InAir ? anim + leg.LandOffset : anim, floor, anim.y);
                        leg.InAir = false;
                        leg.Lead = Mathf.Max(0f, Vector3.Dot(Flat(leg.Pin - anim), dir));
                    }
                    if (follow || SlideAsDanced && !InPlaceMode)
                    {
                        // Follow the dance on the floor (tools/follow_sim.py). Where the dance drags the foot back, the
                        // body walks over it and it stays put; where the dance slides it forward it slides just as far
                        // as standing - a bit more (up to twice) only while it trails the dance by over 0.15 leg, so a
                        // foot the dance never lifts (Go Mufasa) keeps up. It never slides forward past the dance.
                        var g = Ground(anim, floor, anim.y);
                        Vector3 rel = anim - pos;
                        // Hop parts (DanceTuning.HopCarry): when the dance lifts the foot even a little, it hops
                        // forward with the body (at most 3 leg lengths/s) instead of staying behind.
                        if (track != null && track.Hop[f] && track.FootLift[i][f] > 0.05f)
                        {
                            float behind = Vector3.Dot(Flat(g - leg.Pin), dir);
                            if (behind > 0f) leg.Pin += dir * Mathf.Min(behind, legLen * 3f * dt);
                        }
                        if (leg.HasLastRel)
                        {
                            float slid = Vector3.Dot(rel - leg.LastRel, dir);
                            if (slid > 0f)
                            {
                                float trail = Vector3.Dot(Flat(g - leg.Pin), dir);
                                leg.Pin += dir * (slid + Mathf.Min(slid, Mathf.Max(0f, trail - legLen * 0.15f)));
                            }
                        }
                        leg.LastRel = rel;
                        leg.HasLastRel = true;
                        // Never further ahead of the dance than it landed (the body then walks over it).
                        float along = Vector3.Dot(Flat(g - leg.Pin), dir);
                        leg.Lead = Mathf.Min(leg.Lead, Mathf.Max(0f, -along));
                        leg.Pin = -along > leg.Lead ? g + dir * leg.Lead : g - dir * along;   // sideways and height as danced
                    }
                    else leg.HasLastRel = false;
                    var gap = Flat(anim - leg.Pin);
                    leg.PlantedFor += dt;
                    // The other foot only blocks a step while it is itself mid-step; if the dance holds it up for a
                    // while (standing on one leg) this foot may hop along on its own.
                    // While the dance holds the other foot up this one must stay down (else the dancer would float);
                    // the pace drops there instead.
                    bool otherBusy = other != null && (other.Stepping || other.InAir);
                    // Swaying dances step with the sway: the hips lean over the other foot, so this one is free.
                    // Left far behind it steps anyway, or the leg would stretch and the hips sink.
                    if (SwaySteps && other != null && _hips != null && gap.magnitude < legLen * FallbackGap * 2f)
                        otherBusy |= Flat(anim - _hips.position).magnitude < Flat(other.Foot.position - _hips.position).magnitude;
                    // The dance keeps this foot down too long (tapping the other foot, standing parts): a calm
                    // step of its own, taking turns with the other foot.
                    if (gap.magnitude > legLen * FallbackGap && leg.PlantedFor > 0.25f && !otherBusy)
                    {
                        leg.Stepping = true;
                        leg.T = 0f;
                        leg.From = leg.Pin;
                        leg.StepAnim = anim;
                        leg.StepBody = pos;
                        if (Diagnose) _stats.OwnSteps[i]++;
                    }
                    target = leg.Pin;
                }

                targets[i] = Vector3.Lerp(anim, target, _weight);
                if (Diagnose)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    leg.Why = string.Format(inv, "frame {0:0.0} ground {1} airU {2:0.00} u0 {3:0.00} lift {4:0.00} land {5:0.00} pin-anim {6:0.00} w {7:0.00}",
                        frame, danceOnGround, airU, leg.AirU0, leg.LiftOffset.magnitude / legLen, leg.LandOffset.magnitude / legLen,
                        Flat(leg.Pin - anim).magnitude / legLen, _weight);
                }
            }

            // If a pinned foot is just out of reach (the dance has the leg nearly straight), lower the hips a little
            // instead of letting the leg stretch and the foot slide. At most an eighth of the leg, eased.
            float need = 0f;
            for (int i = 0; i < _legs.Length; i++)
            {
                var leg = _legs[i];
                if (leg == null) continue;
                Vector3 hip = leg.Up.position;
                float reach = (leg.UpperLen + leg.LowerLen) * 0.99f;
                Vector3 flat = Flat(targets[i] - hip);
                float down = hip.y - targets[i].y;
                float allowed = flat.magnitude < reach ? Mathf.Sqrt(reach * reach - flat.sqrMagnitude) : 0f;
                need = Mathf.Max(need, down - allowed);
            }
            float maxDrop = LegLength * 0.12f;
            _hipDrop = Mathf.MoveTowards(_hipDrop, Mathf.Clamp(need, 0f, maxDrop) * _weight, dt * LegLength * 1.5f);
            if (_hips != null && _hipDrop > 0f) _hips.position -= Vector3.up * _hipDrop;

            if (Diagnose) Measure(targets, dt);
            else foreach (var l in _legs) if (l != null) l.HasLastTarget = false;
            for (int i = 0; i < _legs.Length; i++)
                if (_legs[i] != null) Solve(_legs[i], targets[i]);
        }

        // ---- Report for tuning (the local player only): what the feet did while walking, every 5 s ----
        public bool Diagnose { get; set; }
        public string Label { get; set; }

        private sealed class Stats
        {
            public float Time, MaxSpeed, Overstretch, MaxStretch, MaxDrop;
            public int Frames, Pops, Flicker, Switches;
            public string MaxSpeedWhat = "";
            public readonly int[] Steps = new int[2], OwnSteps = new int[2];
        }

        private Stats _stats = new Stats();
        private bool _lastInPlace;

        private void Measure(Vector3[] targets, float dt)
        {
            var s = _stats;
            s.Time += dt;
            s.Frames++;
            if (InPlaceMode != _lastInPlace) s.Switches++;
            _lastInPlace = InPlaceMode;
            s.MaxDrop = Mathf.Max(s.MaxDrop, _hipDrop / LegLength);
            bool stretched = false;
            for (int i = 0; i < _legs.Length; i++)
            {
                var leg = _legs[i];
                if (leg == null) continue;
                float len = leg.UpperLen + leg.LowerLen;
                float over = ((targets[i] - leg.Up.position).magnitude - len) / len;
                if (over > 0.01f) { stretched = true; s.MaxStretch = Mathf.Max(s.MaxStretch, over); }
                // A planted foot should stand still; one in the air moves with the body. Jumps show up as speed.
                string state = leg.Stepping ? "own step" : leg.InAir ? "air" : "planted";
                if (leg.HasLastTarget)
                {
                    float v = (targets[i] - leg.LastTarget).magnitude / dt / len;
                    float danced = (leg.Foot.position - leg.LastAnim).magnitude / dt / len;
                    if (v > danced + 4f)
                    {
                        s.Pops++;
                        if (s.Pops <= 3)
                        {
                            Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Feet pop {0}: {1:0.0} leg/s ({2:0.000} leg in {3:0.0000}s) {4} -> {5}, dance foot moved {6:0.000} leg; {7}",
                                i == 0 ? "L" : "R", v, (targets[i] - leg.LastTarget).magnitude / len, dt, leg.LastState, state,
                                (leg.Foot.position - leg.LastAnim).magnitude / len, leg.Why));
                        }
                    }
                    if (v > s.MaxSpeed)
                    {
                        s.MaxSpeed = v;
                        s.MaxSpeedWhat = (i == 0 ? "L " : "R ") + (leg.Stepping ? "own step" : leg.InAir ? "air" : "planted");
                    }
                }
                leg.LastTarget = targets[i];
                leg.HasLastTarget = true;
                leg.LastState = state;
                leg.LastAnim = leg.Foot.position;
            }
            if (stretched) s.Overstretch++;
            if (s.Time < 5f) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Plugin.Log.LogInfo(string.Format(inv,
                "Feet ({0}): steps L{1} R{2}, own steps L{3} R{4}, flicker {5}, pops {6} (max {7:0.0} leg/s, {8}), overstretched {9:0}% (max {10:0.00} leg), hips down max {11:0.00} leg, sideways switches {12}",
                Label, s.Steps[0], s.Steps[1], s.OwnSteps[0], s.OwnSteps[1], s.Flicker, s.Pops, s.MaxSpeed, s.MaxSpeedWhat,
                100f * s.Overstretch / Mathf.Max(1, s.Frames), s.MaxStretch, s.MaxDrop, s.Switches));
            _stats = new Stats();
        }

        // Slide steps (DanceTuning.SlideWalk): each foot in turn slides along the floor from where it stood to a quarter
        // stride ahead of its hip, then stands while the body walks over it (to a quarter stride behind). Half the cycle
        // sliding, half standing; the two feet half a cycle apart.
        private Vector3 SlideStep(Leg leg, int i, Vector3 anim, Vector3 pos, Vector3 dir)
        {
            float legLen = leg.UpperLen + leg.LowerLen, floor = pos.y + leg.AnkleRest;
            float phase = Mathf.Repeat(_slidePhase + i * 0.5f, 1f);
            bool sliding = phase < 0.5f;
            Vector3 hip = leg.Up.position;
            var ahead = Ground(new Vector3(hip.x, floor, hip.z) + dir * (Stride * legLen * 0.25f), floor, floor);
            if (!leg.Planted && !leg.Stepping) { leg.Pin = Ground(anim, floor, anim.y); leg.Planted = true; }
            if (sliding)
            {
                if (!leg.Stepping) { leg.Stepping = true; leg.From = leg.Pin; }
                float u = phase / 0.5f, e = u * u * (3f - 2f * u);
                leg.Pin = Vector3.Lerp(leg.From, ahead, e);
                // a walking step lifts the foot in an arc (the pin itself stays on the floor)
                if (WalkLift > 0f) return leg.Pin + Vector3.up * Mathf.Sin(u * 3.14159265f) * WalkLift * legLen;
            }
            else leg.Stepping = false;
            return leg.Pin;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // A planted foot sits at the dance's ankle height, but never under the floor the player stands on.
        private static Vector3 Ground(Vector3 p, float minY, float danceY) => new Vector3(p.x, Mathf.Max(minY, Mathf.Min(danceY, minY + 0.02f)), p.z);

        /// <summary>Two-bone IK: bend the leg so the ankle reaches <paramref name="target"/>, knee pointing where the dance had it.</summary>
        private static void Solve(Leg leg, Vector3 target)
        {
            var footRot = leg.Foot.rotation;
            Vector3 hip = leg.Up.position;
            float a = leg.UpperLen, b = leg.LowerLen;
            Vector3 toTarget = target - hip;
            float d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
            Vector3 dir = toTarget.normalized;
            Vector3 pole = leg.Knee.position - hip;
            Vector3 bend = pole - dir * Vector3.Dot(pole, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.Cross(dir, leg.Up.right);
            bend.Normalize();
            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 knee = hip + dir * (cosA * a) + bend * (sinA * a);

            leg.Up.rotation = Quaternion.FromToRotation(leg.Knee.position - hip, knee - hip) * leg.Up.rotation;
            Vector3 ankle = hip + dir * d;
            leg.Knee.rotation = Quaternion.FromToRotation(leg.Foot.position - leg.Knee.position, ankle - leg.Knee.position) * leg.Knee.rotation;
            leg.Foot.rotation = footRot;
        }
    }
}
