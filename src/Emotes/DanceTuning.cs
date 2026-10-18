using System.Collections.Generic;

namespace Emotes
{
    /// <summary>
    /// Per-dance tweaks for walking in a dance, tuned one dance at a time against videos of the dance standing still
    /// (how it should look) and walking. Dances not listed use the defaults.
    /// </summary>
    internal sealed class DanceTuning
    {
        /// <summary>Share of the dance's own up/down hip motion kept while walking (hops, bounces).</summary>
        public float HipBounce = 0.25f;

        /// <summary>Share of the dance's own sideways/forward hip motion kept while walking.</summary>
        public float HipSway = 0.1f;

        /// <summary>Share of the sideways part alone, when it differs (a dance whose hips swing side to side); -1 = HipSway.</summary>
        public float HipSide = -1f;

        /// <summary>Walking speed never drops below this (leg lengths/s), for dances that stand a long while.</summary>
        public float MinPace;

        /// <summary>A foot left this far behind (leg lengths) takes a step of its own.</summary>
        public float FallbackGap = 0.55f;

        /// <summary>Those steps go with the hips' sway: only the foot the hips lean away from (the unweighted one) steps.</summary>
        public bool SwaySteps;

        /// <summary>
        /// A foot down follows the dance sideways and in height, and forwards as far as the dance slides it standing
        /// (FollowDance does this anyway): for dances that shuffle their feet about on the floor.
        /// </summary>
        public bool SlideAsDanced;

        /// <summary>Extra lift on steps (1 = full; 0 = only the dance's own lift, for dances that lift the feet high).</summary>
        public float StepArc = 1f;

        /// <summary>
        /// Walking forward the legs move exactly as danced: a foot down follows the dance (it may scrape backwards like
        /// it does standing, never slide forwards), a foot up goes where the dance puts it, and the walking speed is
        /// what the dance's scrapes push (see GaitTrack.BuildScrapePace). For dances whose feet scrape back a lot.
        /// </summary>
        public bool FollowDance;

        /// <summary>
        /// With FollowDance: in parts where the dance hops (hips up high) the body keeps going instead of stopping at
        /// each landing, and a foot the dance lifts even a little hops forward with it.
        /// </summary>
        public bool HopCarry;

        /// <summary>
        /// With FollowDance: where the dance doesn't push the body along (it faces sideways, or just steps), its steps
        /// carry it like a walk - the speed is whichever is faster, and those steps land a bit ahead.
        /// </summary>
        public bool StepAssist;

        /// <summary>
        /// A foot counts as lifted once this high (leg lengths); 0 = judged from how high the dance lifts it at most.
        /// For dances mixing big jumps with small steps (the small ones would count as standing).
        /// </summary>
        public float ContactLift;

        /// <summary>The legs stay exactly as danced while walking, feet gliding along (roller skates).</summary>
        public bool NoFootIK;

        /// <summary>
        /// With FollowDance: every push speeds the body up and it then glides on, slowing over <see cref="Coast"/>
        /// seconds, instead of each foot setting the speed (skating).
        /// </summary>
        public bool Glide;

        /// <summary>Seconds for the speed to fall to about a third without a push (FollowDance).</summary>
        public float Coast = 0.25f;

        /// <summary>
        /// 0 = the body takes the dance's speed at once (every push shows). Otherwise how quickly (per second) it
        /// eases to it, starting and stopping too - momentum, like gliding on skates.
        /// </summary>
        public float Momentum;

        /// <summary>Roller skates pop onto the dancer's feet for this dance (RollerSkates).</summary>
        public bool Skates;

        /// <summary>Leans into turns while walking, from the floor, by how hard the turn is at that speed (skates do too).</summary>
        public bool LeanIntoTurns;

        /// <summary>
        /// Walked by hops: every time a foot leaves the floor the body springs forward at this speed (leg lengths/s)
        /// and then runs on, slowing over <see cref="Coast"/> seconds. 0 = off.
        /// </summary>
        public float HopPush;

        /// <summary>The walking speed from the dance, scaled (with feet left as danced, faster than the dance pushes).</summary>
        public float SpeedScale = 1f;

        /// <summary>
        /// Walked by slide steps: where the dance moves a foot quickly along the floor, that foot slides forward (no
        /// lift), then the other one is drawn up at its next slide. <see cref="SlideLength"/>: the longest slide.
        /// </summary>
        public bool SlideSteps;
        public float SlideLength = 0.8f;

        /// <summary>
        /// While walking, the legs do a move of their own instead of the dance's (the body keeps dancing): slide steps -
        /// one foot slides along the floor from behind to ahead while the other stands, then the other is drawn up past
        /// it. <see cref="Stride"/>: how far each slide goes (leg lengths); <see cref="WalkSpeed"/>: world units/s.
        /// </summary>
        /// <summary>
        /// A foot the dance keeps down long hops forward at the top of each bounce of the dance (on one leg if the other
        /// is up), for dances that bounce on the spot.
        /// </summary>
        public bool BounceHops;

        /// <summary>With BounceHops: each hop flies further, the walking speed rising by this much (leg lengths/s) round it.</summary>
        public float BounceBoost;

        /// <summary>
        /// Jumps out of the dance's deep squats: both feet leave the floor as it rises, and the walking speed goes up by
        /// this much (leg lengths/s) over the jump so it lands further. 0 = off.
        /// </summary>
        public float SquatJumps;

        /// <summary>
        /// Real hopping: the speed comes from the hops themselves - each lift-off throws the body forward at HopPush (a
        /// jump out of a squat at SquatJumps), in the air it carries on unchanged, on the floor friction slows it over
        /// <see cref="GroundDrag"/> seconds.
        /// </summary>
        public bool HopPhysics;
        public float GroundDrag = 0.35f;

        public bool SlideWalk;
        public float Stride = 0.8f;
        public float WalkSpeed;

        /// <summary>With SlideWalk: how high (leg lengths) the moving foot lifts - 0 slides it along the floor, more walks.</summary>
        public float WalkLift;

        /// <summary>With FollowDance: how many seconds either side the walking speed is evened out over (quick
        /// footwork pushes in short bursts that would make it jerky).</summary>
        public float SpeedSmoothing = 0.07f;

        public static readonly DanceTuning Default = new DanceTuning();

        private static readonly Dictionary<string, DanceTuning> ByKey = new Dictionary<string, DanceTuning>
        {
            // Hops on one foot while the other taps the heel forward and scrapes it back fast (~2 leg lengths/s): that
            // scrape is what walks you forward. Keep the hop whole and let the legs do exactly the dance.
            [DanceMusic.Key("Get Griddy")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true },
            // Three parts: one foot swivels on the floor (never lifted) while the other taps; high kicking steps;
            // a wide shuffle with both feet sliding. Its feet slide on the floor a lot, so it walks by them too.
            [DanceMusic.Key("Go Mufasa")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true },
            // Walks in place sliding the feet back and forth (like a moonwalk) with a few jumps (pulling the fish in):
            // the slides push it along at about a leg length per second, slowing for the jumps.
            [DanceMusic.Key("Fishin'")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true, HopCarry = true },
            // Quick shuffle footwork, the feet hardly leave the floor and slide every way: it shuffles along slowly
            // (about 0.4 leg lengths/s), its many tiny pushes evened out over a fifth of a second.
            [DanceMusic.Key("Fast Feet")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true, SpeedSmoothing = 0.2f },
            // Side steps drifting backwards, then a long sway on both feet, then a few bigger steps. Its feet don't
            // scrape, so it walks by steps: each lift is a step forward; in the sway it keeps strolling with small
            // steps of the foot the hips lean away from. The sway itself stays whole.
            // Horse riding: both feet hop together in a wide stance (gallops forward walking); then one foot steps
            // forward and slides back while the other stands behind (that slide pushes it along).
            // The second half is danced side-on, so there its steps carry it (it walks sideways to the dance).
            [DanceMusic.Key("Gangnam Style")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true, HopCarry = true, StepAssist = true, SpeedSmoothing = 0.15f, ContactLift = 0.07f },
            // A scoot: both feet scrape back together (that carries it forward), then a little hop puts them ahead again.
            [DanceMusic.Key("Wind Up")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true },
            // A slow gliding walk on the spot (each foot slides back a whole second, then swings forward low): walked
            // just like that, the brief hold in each stride evened out so it never stops.
            [DanceMusic.Key("Slide Stride")] = new DanceTuning { HipBounce = 1f, StepArc = 0f, FollowDance = true, SpeedSmoothing = 0.25f },
            // Roller skating: the feet glide about and the hips sway wide. Skates roll, so the legs stay as danced; every
            // push back speeds it up and it glides on.
            [DanceMusic.Key("Roller Vibes")] = new DanceTuning
            {
                HipBounce = 1f, HipSway = 1f, FollowDance = true, NoFootIK = true, Glide = true, Coast = 1.2f, SpeedSmoothing = 0.15f,
                Skates = true,   // and skating physics (SkatePhysicsPatch)
            },
            // Kick-starting a dirt bike: standing on the left foot, tapping it in time, the right leg up and kicking the
            // starter now and then. It hops along on the left foot (each tap a little hop forward, the step arc shows the
            // hop) and the kicking foot lands ahead. Each hop springs it forward, then it runs on (about 0.7 leg lengths/s).
            [DanceMusic.Key("Dirtbike Challenge")] = new DanceTuning { HipBounce = 1f, SlideAsDanced = true, HopPush = 1.2f, Coast = 0.3f },
            // Slides the feet about on the floor, swaying the hips wide from side to side. Walking, the legs slide-step
            // forward on their own (one foot slides ahead, the other is drawn up past it) at 1.1 units/s; the sway stays.
            [DanceMusic.Key("Freedom Wheels")] = new DanceTuning
            {
                HipBounce = 1f, HipSide = 1f, SlideWalk = true, Stride = 1f, WalkSpeed = 1.1f,
            },
            // Twisting steps, then bouncing in a wide stance (both feet leave the floor a little), then a big kick. Steps
            // carry it (the bounces count as little hops forward, the kick lands ahead); about 0.35-0.85 leg lengths/s.
            [DanceMusic.Key("In Da Party")] = new DanceTuning
            {
                HipBounce = 1f, StepArc = 0.5f, FollowDance = true, StepAssist = true, ContactLift = 0.07f, SpeedSmoothing = 0.15f,
            },
            // Marching on the spot, knees up in turn, feet close under the body: it marches forward - every step pushes it
            // on and it runs on between (about 0.8 leg lengths/s), each foot landing ahead.
            [DanceMusic.Key("I Like To Move It")] = new DanceTuning { HipBounce = 1f, SlideAsDanced = true, HopPush = 1.4f, Coast = 0.4f },
            // Stomping about on the left foot (taps, steps, two deep squats) while the right stands; it bounces slowly.
            // The standing foot hops forward at the top of each bounce (one-legged when the other is up), the left steps
            // carry it too; about 0.7 leg lengths/s.
            // Real hopping physics: each hop throws it forward, it flies on unchanged, the floor slows it; out of a deep
            // squat it jumps much further (about 1.3 leg lengths/s overall).
            [DanceMusic.Key("Rushin' Around")] = new DanceTuning
            {
                HipBounce = 1f, SlideAsDanced = true, BounceHops = true, HopPhysics = true, HopPush = 1.6f, SquatJumps = 2.6f,
                GroundDrag = 0.35f,
            },
            // A child's airplane: arms out like wings, banking, quick little running steps. It runs along - every step
            // throws it on (hop physics: about 1.1 units/s), the steps land ahead, and it banks into turns.
            [DanceMusic.Key("The Airplane")] = new DanceTuning
            {
                HipBounce = 1f, SlideAsDanced = true, HopPhysics = true, HopPush = 1.6f, GroundDrag = 0.4f, LeanIntoTurns = true,
            },
            // A catwalk strut on the spot: quick steps placed in a line, hips swaying, shoulders twisting. It struts forward:
            // every step pushes it on and it runs on smoothly between (about 0.95 leg lengths/s); the hip sway stays.
            [DanceMusic.Key("Walkin' Pretty")] = new DanceTuning
            {
                HipBounce = 1f, HipSide = 1f, SlideAsDanced = true, HopPush = 1.3f, Coast = 0.6f, SpeedSmoothing = 0.15f,
            },
            // A long routine: spins, high kicks, standing on one leg. Danced as closely as a real person would on the move:
            // the legs do the dance (kicks land ahead), every lift-off throws the body on, it carries on in the air and the
            // floor slows it; standing on one leg it hops at the top of each bounce; it leans into turns (about 0.9 u/s).
            [DanceMusic.Key("Wake Up")] = new DanceTuning
            {
                HipBounce = 1f, SlideAsDanced = true, ContactLift = 0.07f, BounceHops = true, HopPhysics = true, HopPush = 1.5f,
                GroundDrag = 0.5f, LeanIntoTurns = true,
            },
            // Slow, dramatic high-knee steps with the body twisting far to each side and the hips swaying. Each knee-up is a
            // big step that throws it on, the floor slows it between: a slow strut (about 0.33 units/s).
            [DanceMusic.Key("Smeeze")] = new DanceTuning
            {
                HipBounce = 1f, HipSide = 1f, SlideAsDanced = true, HopPhysics = true, HopPush = 0.8f, GroundDrag = 0.4f,
            },
            [DanceMusic.Key("Forget Me Not")] = new DanceTuning { HipBounce = 1f, HipSide = 1f, MinPace = 0.25f, FallbackGap = 0.35f, SwaySteps = true, SlideAsDanced = true },
        };

        public static DanceTuning For(Dance d) =>
            d != null && ByKey.TryGetValue(DanceMusic.Key(d.Label), out var t) ? t : Default;
    }
}
