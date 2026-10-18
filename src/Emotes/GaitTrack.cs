using System;
using System.Collections.Generic;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// What a dance does with its feet, worked out from the animation on this character's skeleton, and how fast
    /// someone doing that dance could walk at each moment (tuned with tools/gait_sim.py):
    ///  - a foot stands while the dance has it down; the dance may itself slide it back (the body walking over it) -
    ///    that part of the walk is free, the foot stays put on its own;
    ///  - beyond that the body may travel at most half a leg past a standing foot (a quarter ahead to a quarter
    ///    behind), so the foot never slides or overstretches; a foot held up for long (a pose, a leg in the air)
    ///    leaves the other one standing, which slows the walk to a stop instead of floating;
    ///  - a foot can only fly as far per step as the dance lifts it (tiny lifts = small steps).
    /// Two versions: walking forward (uses the dance's own foot slide) and any other direction (in place).
    /// </summary>
    internal sealed class GaitTrack
    {
        public readonly int Frames, LoopStart;
        public readonly float Fps;
        public readonly bool[][] Contact = new bool[2][];     // [foot][frame]
        public readonly int[][] AirStart = new int[2][];      // for air frames: frame the foot left the ground
        public readonly int[][] AirEnd = new int[2][];        // for air frames: frame it lands (may run past the end)
        public readonly float[] Pace, PaceInPlace;            // allowed walking speed, leg lengths per second
        public readonly float[][] LandAhead = new float[2][]; // for air frames: land this far ahead (leg lengths, forward)
        public readonly float[][] LandAheadInPlace = new float[2][];
        public int StepsPerLoop, LongestStill;                // for the report
        public float FacingDot;                               // dance forward (toes) vs the player's facing, for the report
        public Vector3 LocalForward;                          // where the dancer faces, in the hips' parent space

        /// <summary>Forward walking pace comes from the dance's own foot scrapes (see <see cref="DanceTuning.FollowDance"/>).</summary>
        public readonly bool FollowDance;

        /// <summary>For hop parts (DanceTuning.HopCarry): the dance's hips are up there, the body carries on through them.</summary>
        public readonly bool[] Hop;

        /// <summary>Foot height above where it stands, leg lengths, per [foot][frame].</summary>
        public readonly float[][] FootLift = new float[2][];

        /// <summary>With FollowDance: steps may carry the body too (DanceTuning.StepAssist).</summary>
        public readonly bool StepAssist;

        /// <param name="model">The character model: walking goes where it faces.</param>
        public static GaitTrack Build(Dance dance, Transform hips, float scale, Transform model)
        {
            try { return new GaitTrack(dance, hips, scale, model, DanceTuning.For(dance)); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Gait analysis failed for {dance.Label}: {e.Message}");
                return null;
            }
        }

        private GaitTrack(Dance d, Transform hips, float scale, Transform model, DanceTuning tuning)
        {
            FollowDance = tuning.FollowDance;
            StepAssist = tuning.FollowDance && tuning.StepAssist;
            Frames = d.Frames;
            LoopStart = d.LoopStart;
            Fps = d.Fps;
            int n = Frames, loopLen = Math.Max(1, Frames - LoopStart);
            var parent = hips.parent;
            Vector3 up = parent.InverseTransformDirection(Vector3.up).normalized;
            Vector3 bodyFwd = parent.InverseTransformDirection(model != null ? model.forward : Vector3.forward);
            bodyFwd = (bodyFwd - up * Vector3.Dot(bodyFwd, up)).normalized;

            int Bone(string name) => Array.IndexOf(d.Bones, "mixamorig:" + name);
            Transform Find(string name)
            {
                foreach (var t in hips.GetComponentsInChildren<Transform>(true)) if (t.name == "mixamorig:" + name) return t;
                return null;
            }
            int Wrap(int f) => f < n ? (f >= 0 ? f : 0) : LoopStart + (f - LoopStart) % loopLen;

            // ---- FK: foot height and forward position (relative to the hips, whose own travel walking damps) ----
            int hipsIdx = Bone("Hips");
            var height = new float[2][];
            var forward = new float[2][];
            var footPos = new Vector3[2][];
            Vector3 toeDir = Vector3.zero;   // where the dancer faces: the way the toes point, summed over the dance
            float legLen = 0f;
            Vector3 hipsStart = hips.localPosition;
            for (int side = 0; side < 2; side++)
            {
                string s = side == 0 ? "Left" : "Right";
                Transform upLeg = Find(s + "UpLeg"), knee = Find(s + "Leg"), foot = Find(s + "Foot"), toe = Find(s + "ToeBase");
                if (upLeg == null || knee == null || foot == null) throw new InvalidOperationException("leg bones missing");
                legLen = knee.localPosition.magnitude + foot.localPosition.magnitude;
                int iUp = Bone(s + "UpLeg"), iKnee = Bone(s + "Leg"), iFoot = Bone(s + "Foot");
                height[side] = new float[n];
                forward[side] = new float[n];
                footPos[side] = new Vector3[n];
                for (int f = 0; f < n; f++)
                {
                    Quaternion rH = hipsIdx >= 0 ? d.Rotations[hipsIdx][f] : hips.localRotation;
                    Vector3 travel = d.HipsOffset[f] * scale;
                    Vector3 pH = hipsStart + travel;
                    Quaternion rUp = rH * (iUp >= 0 ? d.Rotations[iUp][f] : upLeg.localRotation);
                    Vector3 pUp = pH + rH * upLeg.localPosition;
                    Quaternion rKnee = rUp * (iKnee >= 0 ? d.Rotations[iKnee][f] : knee.localRotation);
                    Vector3 pKnee = pUp + rUp * knee.localPosition;
                    Quaternion rFoot = rKnee * (iFoot >= 0 ? d.Rotations[iFoot][f] : foot.localRotation);
                    Vector3 pFoot = pKnee + rKnee * foot.localPosition;
                    float h = Vector3.Dot(pFoot, up);
                    if (toe != null)
                    {
                        Vector3 t = rFoot * toe.localPosition;
                        h = Mathf.Min(h, Vector3.Dot(pFoot + t, up));
                        toeDir += t - up * Vector3.Dot(t, up);
                    }
                    height[side][f] = h;
                    footPos[side][f] = pFoot - travel;
                }
            }
            // Walking goes where the character model faces. The dance may turn the body (Gangnam Style dances its
            // second half side-on): its feet are measured along the way the player actually walks.
            Vector3 fwd = bodyFwd;
            FacingDot = toeDir.sqrMagnitude > 1e-6f ? Vector3.Dot(toeDir.normalized, bodyFwd) : 1f;
            LocalForward = fwd;
            for (int side = 0; side < 2; side++)
                for (int f = 0; f < n; f++) forward[side][f] = Vector3.Dot(footPos[side][f], fwd) / legLen;

            // ---- Ground contact (hysteresis, relative to this foot's movement; 1-2 frame blips removed) ----
            var lift = new float[2];
            for (int side = 0; side < 2; side++)
            {
                var h = height[side];
                var sorted = (float[])h.Clone();
                Array.Sort(sorted);
                float lo = sorted[(int)(n * 0.05f)], hi = sorted[(int)(n * 0.95f)];
                lift[side] = (hi - lo) / legLen;
                float amp = Mathf.Max(hi - lo, legLen * 0.03f);
                FootLift[side] = new float[n];
                for (int f = 0; f < n; f++) FootLift[side][f] = Mathf.Max(0f, h[f] - lo) / legLen;
                float enter = lo + Mathf.Max(amp * 0.22f, legLen * 0.02f), exit = lo + Mathf.Max(amp * 0.38f, legLen * 0.04f);
                if (tuning.ContactLift > 0f) { exit = lo + tuning.ContactLift * legLen; enter = lo + tuning.ContactLift * 0.57f * legLen; }
                var c = new bool[n];
                bool on = h[0] < exit;
                for (int f = 0; f < n; f++)
                {
                    if (on && h[f] > exit) on = false;
                    else if (!on && h[f] < enter) on = true;
                    c[f] = on;
                }
                for (int pass = 0; pass < 2; pass++)
                    for (int f = 1; f < n - 1; f++)
                    {
                        int run = 0;
                        while (f + run < n && c[f + run] != c[f - 1]) run++;
                        if (run > 0 && run <= 2 && f + run < n)
                            for (int k = 0; k < run; k++) c[f + k] = c[f - 1];
                    }
                Contact[side] = c;
            }

            if (tuning.SlideSteps) PickSlides(footPos, up, legLen);
            var hops = tuning.BounceHops ? AddBounceHops(d, scale, up, legLen) : null;
            var squats = tuning.SquatJumps > 0f ? AddSquatJumps(d, scale, up, legLen) : null;

            // ---- Per foot: stance length, step cycle, the dance's own slide while standing ----
            var stance = new int[2][];
            var cycle = new int[2][];
            var slide = new float[2][];
            var lifts = new List<int>();
            for (int side = 0; side < 2; side++)
            {
                var c = Contact[side];
                stance[side] = new int[n];
                cycle[side] = new int[n];
                slide[side] = new float[n];
                var spans = new List<(int a, int b, bool on)>();
                for (int f = 0; f < n;)
                {
                    int g = f;
                    while (g < n && c[g] == c[f]) g++;
                    spans.Add((f, g, c[f]));
                    f = g;
                }
                for (int i = 0; i < spans.Count; i++)
                {
                    var (a, b, on) = spans[i];
                    if (on)
                    {
                        // average backward speed of this foot over the stance (the dance walking over it)
                        float m = 0f;
                        for (int k = a; k < b; k++)
                            m += -(forward[side][Math.Min(n - 1, k + 1)] - forward[side][Math.Max(0, k - 1)]) * Fps / 2f;
                        m = Mathf.Clamp(m / (b - a), -0.5f, 2f);
                        for (int k = a; k < b; k++) { stance[side][k] = b - a; slide[side][k] = m; }
                    }
                    else
                    {
                        var next = spans[(i + 1) % spans.Count];
                        int len = next.on ? next.b - next.a : 1;
                        for (int k = a; k < b; k++) stance[side][k] = Math.Max(1, len);
                    }
                }
                var footLifts = new List<int>();
                for (int f = 1; f < n; f++) if (c[f - 1] && !c[f]) { footLifts.Add(f); lifts.Add(f); }
                for (int f = 0; f < n; f++) cycle[side][f] = n;
                for (int i = 0; i + 1 < footLifts.Count; i++)
                    for (int k = footLifts[i]; k < footLifts[i + 1]; k++) cycle[side][k] = footLifts[i + 1] - footLifts[i];

                AirStart[side] = new int[n];
                AirEnd[side] = new int[n];
                for (int f = 0; f < n; f++)
                {
                    if (c[f]) continue;
                    int s = f;
                    while (s > 0 && !c[s - 1] && s - 1 >= (f >= LoopStart ? LoopStart : 0)) s--;
                    // A lift running over the loop's end into its start: it began that many frames before the start.
                    if (s == LoopStart && f >= LoopStart && !c[n - 1])
                    {
                        int k = n - 1;
                        while (k > LoopStart && !c[k]) k--;
                        s = LoopStart - (n - 1 - k);
                    }
                    int e = f, guard = 0;
                    while (!c[Wrap(e)] && guard++ < n * 2) e++;
                    AirStart[side][f] = s;
                    AirEnd[side][f] = e;
                }
            }
            lifts.Sort();
            StepsPerLoop = lifts.FindAll(f => f >= LoopStart).Count;
            int last = -1, longest = 0;
            foreach (int l in lifts) { if (last >= 0) longest = Math.Max(longest, l - last); last = l; }
            LongestStill = lifts.Count == 0 ? n : longest;

            // Hop parts: hips over 0.15 leg above their usual height, give or take a quarter second.
            Hop = new bool[n];
            if (tuning.HopCarry)
            {
                var hy = new float[n];
                for (int f = 0; f < n; f++) hy[f] = Vector3.Dot(d.HipsOffset[f] * scale, up) / legLen;
                var sortedY = (float[])hy.Clone();
                Array.Sort(sortedY);
                float usual = sortedY[n / 2];
                int hw = Mathf.RoundToInt(Fps * 0.25f);
                for (int f = 0; f < n; f++)
                    if (hy[f] - usual > 0.15f)
                        for (int k = Math.Max(0, f - hw); k <= Math.Min(n - 1, f + hw); k++) Hop[k] = true;
            }
            Pace = FollowDance ? BuildScrapePace(forward, tuning) : BuildPace(true, stance, cycle, slide, lift);
            if (tuning.HopPhysics) Pace = BuildHopPhysics(squats, tuning);
            else if (tuning.HopPush > 0f && !FollowDance) Pace = BuildHopPace(lifts, tuning);
            if (tuning.SlideSteps) Pace = BuildSlidePace(cycle, tuning.SlideLength);
            if (hops != null && tuning.BounceBoost > 0f && !tuning.HopPhysics)
            {
                // Hopping on one leg the body may fly a bit further each hop.
                int len = Mathf.RoundToInt(Fps * 0.5f);
                foreach (int p in hops)
                    for (int k = 0; k < len; k++)
                    {
                        int g = p - len / 2 + k;
                        if (g < 0) continue;
                        if (g >= n) g = LoopStart + (g - LoopStart) % Math.Max(1, n - LoopStart);
                        Pace[g] = Mathf.Max(Pace[g], Pace[g] + tuning.BounceBoost * Mathf.Sin(3.14159265f * k / len));
                    }
            }
            if (squats != null && !tuning.HopPhysics)
            {
                // A jump out of a squat goes further: the speed rises over the jump and falls off after it.
                int len = Mathf.RoundToInt(Fps * 0.7f);
                foreach (int b in squats)
                    for (int k = 0; k < len; k++)
                    {
                        int g = b + k;
                        if (g >= n) g = LoopStart + (g - LoopStart) % Math.Max(1, n - LoopStart);
                        Pace[g] += tuning.SquatJumps * Mathf.Sin(3.14159265f * k / len);
                    }
            }
            if (tuning.SpeedScale != 1f) for (int f = 0; f < n; f++) Pace[f] *= tuning.SpeedScale;
            if (StepAssist)
            {
                // Where the dance doesn't push the body along, its steps may: whichever carries it faster.
                var steps = BuildPace(true, stance, cycle, slide, lift);
                for (int f = 0; f < n; f++) Pace[f] = Mathf.Max(Pace[f], steps[f]);
            }
            PaceInPlace = BuildPace(false, stance, cycle, slide, lift);
            if (tuning.MinPace > 0f)
                for (int f = 0; f < n; f++)
                {
                    Pace[f] = Mathf.Max(Pace[f], tuning.MinPace);
                    PaceInPlace[f] = Mathf.Max(PaceInPlace[f], tuning.MinPace);
                }
            for (int side = 0; side < 2; side++)
            {
                LandAhead[side] = BuildLandAhead(side, Pace, slide[side], true);
                // Walked by steps on top of pushes: a step lands at most a third of a leg ahead, in the leg's reach.
                if (StepAssist)
                    for (int f = 0; f < n; f++) LandAhead[side][f] = Mathf.Min(LandAhead[side][f], 0.33f);
                LandAheadInPlace[side] = BuildLandAhead(side, PaceInPlace, slide[side], false);
            }
        }

        private float[] BuildPace(bool useSlide, int[][] stance, int[][] cycle, float[][] slide, float[] lift)
        {
            int n = Frames;
            var raw = new float[n];
            for (int f = 0; f < n; f++)
            {
                float lim = 2.6f;
                for (int side = 0; side < 2; side++)
                {
                    float own = useSlide ? slide[side][f] : 0f;
                    float travel = Mathf.Min(0.6f, lift[side] * 4f);
                    lim = Mathf.Min(lim, Mathf.Max(0f, own) + travel / Mathf.Max(cycle[side][f] / Fps, 0.001f));
                    if (Contact[side][f]) lim = Mathf.Min(lim, own + 0.5f / Mathf.Max(stance[side][f] / Fps, 0.001f));
                }
                raw[f] = Mathf.Max(0f, lim);
            }
            // The worst moment nearby decides (±0.3 s), then a light blur (±0.15 s) so the speed changes smoothly.
            int mw = Mathf.RoundToInt(Fps * 0.3f), bw = Mathf.RoundToInt(Fps * 0.15f);
            var mins = new float[n];
            for (int f = 0; f < n; f++)
            {
                float m = float.MaxValue;
                for (int k = -mw; k <= mw; k++) { int g = f + k; if (g >= 0 && g < n) m = Math.Min(m, raw[g]); }
                mins[f] = m;
            }
            var pace = new float[n];
            for (int f = 0; f < n; f++)
            {
                float sum = 0; int cnt = 0;
                for (int k = -bw; k <= bw; k++) { int g = f + k; if (g >= 0 && g < n) { sum += mins[g]; cnt++; } }
                pace[f] = sum / Math.Max(1, cnt);
            }
            return pace;
        }

        // The dance's own walk, moment by moment: while a foot is down and the dance drags it back, the body moves exactly
        // that fast (the foot stays still on the floor, the leg exactly as danced) - a quick push is a quick surge.
        // Between pushes (hops, both feet up) the body coasts and slows down gently; a light blur keeps it smooth.
        private float[] BuildScrapePace(float[][] forward, DanceTuning tuning)
        {
            int n = Frames, loopLen = Math.Max(1, n - LoopStart);
            var raw = new float[n];
            for (int f = 0; f < n; f++)
            {
                raw[f] = -1f;   // no foot pushing
                for (int side = 0; side < 2; side++)
                {
                    if (!Contact[side][f] || f == 0 || f == n - 1 || !Contact[side][f - 1] || !Contact[side][f + 1]) continue;
                    raw[f] = Mathf.Max(raw[f], Mathf.Max(0f, -(forward[side][f + 1] - forward[side][f - 1]) * Fps / 2f));
                }
            }
            float coast = Mathf.Max(0.05f, tuning.Coast);   // seconds for the speed to fall to ~37 % without a push
            const float hopCoast = 0.5f; // in hop parts the body carries on, a foot down doesn't stop it
            float decay = Mathf.Exp(-1f / (Fps * coast)), hopDecay = Mathf.Exp(-1f / (Fps * hopCoast)), v = 0f;
            var filled = new float[n];
            for (int pass = 0; pass < 2; pass++)   // twice, so the loop's start coasts on from its end
                for (int f = pass == 0 ? 0 : LoopStart; f < n; f++)
                {
                    v = tuning.Glide ? Mathf.Max(Mathf.Max(0f, raw[f]), v * decay)
                        : Hop[f] ? Mathf.Max(Mathf.Max(0f, raw[f]), v * hopDecay) : raw[f] >= 0f ? raw[f] : v * decay;
                    filled[f] = v;
                }
            var pace = new float[n];
            int w = Math.Max(1, Mathf.RoundToInt(Fps * tuning.SpeedSmoothing));
            for (int f = 0; f < n; f++)
            {
                float sum = 0f;
                for (int k = -w; k <= w; k++)
                {
                    int g = f + k;
                    g = g < 0 ? 0 : g < n ? g : LoopStart + (g - LoopStart) % loopLen;
                    sum += filled[g];
                }
                pace[f] = Mathf.Min(4f, sum / (2 * w + 1));
            }
            return pace;
        }

        // Squat jumps (DanceTuning.SquatJumps): where the dance squats deep (hips over a fifth of a leg below their usual
        // height), both feet leave the floor for a quarter second as it comes back up - a jump. Returns the squats.
        private List<int> AddSquatJumps(Dance d, float scale, Vector3 up, float legLen)
        {
            int n = Frames, w = Mathf.Max(2, Mathf.RoundToInt(Fps * 0.25f));
            var hy = new float[n];
            for (int f = 0; f < n; f++) hy[f] = Vector3.Dot(d.HipsOffset[f] * scale, up) / legLen;
            var sorted = (float[])hy.Clone();
            Array.Sort(sorted);
            float usual = sorted[n / 2];
            var bottoms = new List<int>();
            for (int f = 0; f < n; f++)
            {
                float lo = float.MaxValue;
                for (int k = -w; k <= w; k++) lo = Mathf.Min(lo, hy[Mathf.Clamp(f + k, 0, n - 1)]);
                if (hy[f] <= lo && usual - hy[f] > 0.2f && (bottoms.Count == 0 || f - bottoms[bottoms.Count - 1] > w)) bottoms.Add(f);
            }
            int from = Mathf.RoundToInt(Fps * 0.1f), to = Mathf.RoundToInt(Fps * 0.35f);
            foreach (int b in bottoms)
                for (int k = b + from; k <= b + to && k < n; k++)
                    for (int side = 0; side < 2; side++) Contact[side][k] = false;
            return bottoms;
        }

        // Bounce hops (DanceTuning.BounceHops): a foot the dance keeps down a long while hops at the top of each of the
        // dance's bounces (the hips' highest points, at least 0.35 s apart): there it counts as up for a fifth of a
        // second, so it can hop forward - on one leg if the other is up.
        private List<int> AddBounceHops(Dance d, float scale, Vector3 up, float legLen)
        {
            int n = Frames, w = Mathf.Max(2, Mathf.RoundToInt(Fps * 0.2f)), half = Mathf.Max(1, Mathf.RoundToInt(Fps * 0.1f));
            var hy = new float[n];
            for (int f = 0; f < n; f++) hy[f] = Vector3.Dot(d.HipsOffset[f] * scale, up) / legLen;
            var peaks = new List<int>();
            var all = new List<int>();
            for (int f = 0; f < n; f++)
            {
                float hi = float.MinValue, lo = float.MaxValue;
                for (int k = -w; k <= w; k++) { float v = hy[Mathf.Clamp(f + k, 0, n - 1)]; hi = Mathf.Max(hi, v); lo = Mathf.Min(lo, v); }
                if (hy[f] >= hi && hi - lo > 0.03f && (peaks.Count == 0 || f - peaks[peaks.Count - 1] >= Fps * 0.35f)) peaks.Add(f);
            }
            for (int side = 0; side < 2; side++)
            {
                var c = Contact[side];
                var add = new List<int>();
                for (int f = 0; f < n;)
                {
                    if (!c[f]) { f++; continue; }
                    int a = f;
                    while (f < n && c[f]) f++;
                    if ((f - a) / Fps <= 0.5f) continue;
                    foreach (int p in peaks)
                        if (p - half > a + 3 && p + half < f - 3) add.Add(p);
                }
                foreach (int p in add)
                    for (int k = p - half; k <= p + half; k++) c[k] = false;
                all.AddRange(add);
            }
            return all;
        }

        // Slide steps (DanceTuning.SlideSteps): the moments the dance moves a foot quickest along the floor become that
        // foot's slides (it counts as "up" there, so it travels from behind to ahead, staying on the floor), taking turns
        // with the other foot; the rest of the time both stand.
        private void PickSlides(Vector3[][] footPos, Vector3 up, float legLen)
        {
            int n = Frames;
            var windows = new List<(int a, int b, int side)>();
            for (int side = 0; side < 2; side++)
            {
                var fast = new bool[n];
                for (int f = 1; f < n - 1; f++)
                {
                    Vector3 d = footPos[side][f + 1] - footPos[side][f - 1];
                    d -= up * Vector3.Dot(d, up);
                    fast[f] = d.magnitude * Fps / 2f / legLen > 1.2f;
                }
                var raw = new List<(int a, int b)>();
                for (int f = 0; f < n;)
                {
                    if (!fast[f]) { f++; continue; }
                    int a = f;
                    while (f < n && fast[f]) f++;
                    if (raw.Count > 0 && a - raw[raw.Count - 1].b <= 3) raw[raw.Count - 1] = (raw[raw.Count - 1].a, f);
                    else raw.Add((a, f));
                }
                foreach (var w in raw)
                    if (w.b - w.a >= 4) windows.Add((w.a, Math.Min(w.b, w.a + (int)(Fps * 0.6f)), side));   // at most 0.6 s
            }
            windows.Sort((x, y) => x.a.CompareTo(y.a));
            for (int side = 0; side < 2; side++)
                for (int f = 0; f < n; f++) Contact[side][f] = true;
            int last = -1, end = -1;
            foreach (var w in windows)
            {
                if (w.side == last || w.a < end) continue;   // take turns, one at a time
                for (int f = w.a; f < w.b; f++) Contact[w.side][f] = false;
                last = w.side;
                end = w.b;
            }
        }

        // Slide steps: each slide carries a foot up to SlideLength leg lengths, so the body walks that far per slide cycle
        // of the slower foot; the worst moment nearby decides, lightly blurred (like BuildPace).
        private float[] BuildSlidePace(int[][] cycle, float slideLength)
        {
            int n = Frames;
            var raw = new float[n];
            for (int f = 0; f < n; f++)
            {
                float lim = 2.6f;
                for (int side = 0; side < 2; side++)
                    lim = Mathf.Min(lim, slideLength / Mathf.Max(cycle[side][f] / Fps, 0.2f));
                raw[f] = lim;
            }
            int mw = Mathf.RoundToInt(Fps * 0.3f), bw = Mathf.RoundToInt(Fps * 0.3f);
            var mins = new float[n];
            for (int f = 0; f < n; f++)
            {
                float m = float.MaxValue;
                for (int k = -mw; k <= mw; k++) { int g = f + k; if (g >= 0 && g < n) m = Math.Min(m, raw[g]); }
                mins[f] = m;
            }
            var pace = new float[n];
            for (int f = 0; f < n; f++)
            {
                float sum = 0; int cnt = 0;
                for (int k = -bw; k <= bw; k++) { int g = f + k; if (g >= 0 && g < n) { sum += mins[g]; cnt++; } }
                pace[f] = sum / Math.Max(1, cnt);
            }
            return pace;
        }

        // Hop physics (DanceTuning.HopPhysics): every time a foot leaves the floor the body is thrown forward (a jump out
        // of a squat harder); in the air nothing slows it, on the floor friction does. Simulated over the loop (twice,
        // so its start carries on from its end), lightly blurred.
        private float[] BuildHopPhysics(List<int> squats, DanceTuning tuning)
        {
            int n = Frames, loopLen = Math.Max(1, n - LoopStart);
            var jump = new bool[n];
            if (squats != null)
            {
                int from = Mathf.RoundToInt(Fps * 0.1f);
                foreach (int b in squats) if (b + from < n) jump[b + from] = true;
            }
            float drag = Mathf.Exp(-1f / (Fps * Mathf.Max(0.05f, tuning.GroundDrag))), v = 0f;
            var raw = new float[n];
            for (int pass = 0; pass < 2; pass++)
                for (int f = pass == 0 ? 0 : LoopStart; f < n; f++)
                {
                    int prev = f > LoopStart ? f - 1 : (pass == 0 ? f : n - 1);
                    bool liftOff = false;
                    for (int side = 0; side < 2; side++) liftOff |= Contact[side][prev] && !Contact[side][f];
                    bool grounded = Contact[0][f] || Contact[1][f];
                    if (jump[f]) v = Mathf.Max(v, tuning.SquatJumps);
                    else if (liftOff) v = Mathf.Max(v, tuning.HopPush);
                    else if (grounded) v *= drag;
                    raw[f] = v;
                }
            var pace = new float[n];
            for (int f = 0; f < n; f++)
            {
                float sum = 0f;
                for (int k = -2; k <= 2; k++)
                {
                    int g = f + k;
                    g = g < 0 ? 0 : g < n ? g : LoopStart + (g - LoopStart) % loopLen;
                    sum += raw[g];
                }
                pace[f] = sum / 5f;
            }
            return pace;
        }

        // Walked by hops: each lift-off springs the body forward, then it runs on and slows down; lightly blurred.
        private float[] BuildHopPace(List<int> lifts, DanceTuning tuning)
        {
            int n = Frames, loopLen = Math.Max(1, n - LoopStart);
            var kick = new bool[n];
            foreach (int f in lifts) kick[f] = true;
            float decay = Mathf.Exp(-1f / (Fps * Mathf.Max(0.05f, tuning.Coast))), v = 0f;
            var filled = new float[n];
            for (int pass = 0; pass < 2; pass++)
                for (int f = pass == 0 ? 0 : LoopStart; f < n; f++)
                {
                    v = kick[f] ? Mathf.Max(v, tuning.HopPush) : v * decay;
                    filled[f] = v;
                }
            var pace = new float[n];
            int w = Math.Max(1, Mathf.RoundToInt(Fps * tuning.SpeedSmoothing));
            for (int f = 0; f < n; f++)
            {
                float sum = 0f;
                for (int k = -w; k <= w; k++)
                {
                    int g = f + k;
                    g = g < 0 ? 0 : g < n ? g : LoopStart + (g - LoopStart) % loopLen;
                    sum += filled[g];
                }
                pace[f] = sum / (2 * w + 1);
            }
            return pace;
        }

        // A landing foot goes ahead by half of what the body will travel during the coming stance beyond what the
        // dance itself slides the foot back - so it stands under the body mid-stance.
        private float[] BuildLandAhead(int side, float[] pace, float[] slide, bool useSlide)
        {
            int n = Frames;
            var c = Contact[side];
            var res = new float[n];
            int loopLen = Math.Max(1, n - LoopStart);
            int Wrap(int f) => f < n ? f : LoopStart + (f - LoopStart) % loopLen;
            for (int f = 0; f < n; f++)
            {
                if (c[f]) continue;
                int g = f, guard = 0;
                while (!c[Wrap(g)] && guard++ < n) g++;
                float dist = 0f;
                guard = 0;
                for (int k = g; c[Wrap(k)] && guard++ < n; k++)
                    dist += (pace[Wrap(k)] - (useSlide ? slide[Wrap(k)] : 0f)) / Fps;
                res[f] = Mathf.Max(0f, dist * 0.5f);
            }
            return res;
        }
    }
}
