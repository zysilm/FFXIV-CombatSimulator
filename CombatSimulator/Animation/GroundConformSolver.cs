// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>
/// Fits a posed skeleton into the space left above the ground when its owner is pushed down into it.
///
/// The skeleton is taken as it is animated, in its own space (Y up, origin at the owner's feet), and
/// the ground is a horizontal plane that has risen through it. Nothing may be below that plane; and
/// a body with no legs to go down on may not rise above where the pose's highest point was either.
///
/// A skeleton is read by its shape alone; no bone is looked up by name. Its core is everything
/// between the bones that branch (a pelvis that carries legs and a spine, a chest that carries arms
/// and a neck) and it moves as one piece. Its limbs are the chains that hang from the core. As the
/// ground rises:
///   - the core stays where the animation holds it, so it comes down with its owner; limbs that
///     stand on the ground keep their ends where they stand, and fold: it crouches;
///   - pushed past what its legs allow, it goes down on all fours: the core tips forward until
///     it is level, the legs turn under it until it kneels on them, and the limbs that hang down
///     from it (arms) keep hanging down, so they meet the ground and hold it up in front;
///   - pushed further still it comes down low over the ground, knees sliding back and arms
///     folding, but not flat: it stays on its limbs, so that whatever moves them still shows.
/// So a biped crouches, goes on all fours, then lies low; a creature whose body is level already
/// only folds its legs. Bones keep their lengths exactly.
///
/// On all fours the animation, which still walks or runs upright, is made a crawl: the knee steps
/// where the foot stepped and is lifted as the foot was, by its hip; the body shifts over the knees
/// it stands on; and the hand across from a stepping leg is lifted with it.
///
/// A solver is kept for one skeleton and solves it frame after frame: it remembers, for a little
/// while, which limbs were legs and how high the hips ride, so that a step does not change what
/// the body is taken to be doing.
/// </summary>
public sealed class GroundConformSolver
{
    // A bone takes part if what hangs from it is at least this share of the pose's height; anything
    // smaller (fingers, toes, ears) is carried rigidly by the bone it hangs from.
    private const float StructuralShare = 0.10f;
    // A bone's thickness, guessed from its length the way the generic ragdoll guesses it.
    private const float RadiusPerLength = 0.22f;
    private const float MinimumRadiusShare = 0.012f;   // of the pose's height
    private const float MaximumRadiusShare = 0.07f;
    // A limb whose end is this low in the pose, and well below where it is attached, is one the body stands on.
    private const float SupportShare = 0.15f;
    // A bone branches, and so belongs to the core, when at least two of the things hanging from it
    // are each this share of the pose's height: legs and a spine, arms and a neck. A wrist with its
    // twist bone, or a head with its hair, does not branch.
    private const float BranchShare = 0.20f;
    // A bone shorter than this share of its limb is not a joint of its own.
    private const float ShortBoneShare = 0.12f;
    // A body goes down on its knees early, not into a deep crouch first: it starts when its hips
    // have come this share of the way down from where they ride to where they are when it kneels,
    // and is on all fours when they are there. A further push of this share of the pose's height
    // brings it low over the ground.
    private const float CrouchShare = 0.25f;
    private const float StageShare = 0.20f;
    // How far back its thighs lean when it is as low as it goes, radians: kneeling they hang down.
    private const float LowestLean = MathF.PI / 3f;
    // Kneeling, as measured on a crawl made by hand: the foot is turned from pointing forward to
    // pointing back and 35 degrees down, toes trailing, and the neck and head are lifted half way
    // back to where they were.
    private const float KneelingFootTurn = 145f * MathF.PI / 180f;
    private const float HeadKept = 0.5f;
    // (A foot that is carried by its shin lies back with it, and is turned this much further on.)
    private const float CarriedFootTurn = 30f * MathF.PI / 180f;
    // Moving on all fours, as measured on the same crawl. A leg is stepping when the animation has
    // drawn it up, shorter from hip to foot than the leg it stands on (fully so at this share of
    // its length: a walk draws the leg it swings up by about that), and has its foot further off
    // the ground than the foot it stands on (by this share of the pose's height).
    private const float DrawnUpShare = 0.06f;
    private const float FootOffShare = 0.01f;
    // What steps is lifted, by a share of the pose's height: a knee by this much, by its hip
    // rolling up (no further than this, radians), and the hand that steps with it by this much.
    private const float KneeLiftShare = 0.028f;
    private const float HandLiftShare = 0.05f;
    private const float MostHipRoll = 18f * MathF.PI / 180f;
    // A thigh that reaches forward has its knee off the ground: from where it is this far forward
    // (the sine of its angle), and lifted all the way where it is this far.
    private const float ReachingFrom = 0.2f;
    private const float ReachingFully = 0.36f;
    // The knee steps as far as the foot stepped, by this share of the angle.
    private const float KneelingSwing = 0.85f;
    // A knee rests nearer the ground than a joint that stands on it: by this share less.
    private const float KneeSink = 0.6f;
    // The shin lies this far up off the ground behind the knee, and this much more when the leg
    // is drawn up all the way in a step.
    private const float ShinRest = 14f * MathF.PI / 180f;
    private const float ShinStep = 18f * MathF.PI / 180f;
    // Bodies differ: arms are longer or shorter against the legs. On all fours the core tips a
    // little further, or less far, until the limbs it stands on in front reach the ground from
    // where it holds them with this share of their length (as measured on the same crawl: arms a
    // little bent), and as low as it goes with this share; no further than this either way, radians.
    private const float ReachKneeling = 0.92f;
    private const float ReachLow = 0.7f;
    private const float MostFit = 0.35f;
    // A hand is put down under its shoulder: this share of the way out to the side that the
    // shoulder is, and no further ahead or behind than its arm at this angle would reach, radians.
    private const float HandsApart = 0.75f;
    private const float MostHandSwing = 35f * MathF.PI / 180f;
    // Kneeling, the thighs stand a little further apart than the animation has them and the
    // shins lie further out to the side behind them (by this much of their length): the feet of
    // a walk are under the body, the knees of a crawl are wider than the hips and the feet wider
    // than the knees.
    private const float ThighsApart = 0.07f;
    private const float ShinsApart = 0.2f;
    // The body shifts this share of the way over the knees it is standing on.
    private const float SwayShare = 0.8f;
    // Leaning back, a thigh's swing lifts its knee; it swings less the further it leans.
    private const float LeanAtHalfSwing = 0.8f;   // the sine of the lean at which it swings half as far
    // A leg whose foot is lifted high in a step is no less a leg: it is remembered as one for
    // this many solves (a running step is over well within it), no longer, so that an arm put
    // to the ground once does not stay a leg.
    private const int LegMemory = 60;
    // Legs are held at about one height: a limb that stands but is held this share of the pose's
    // height over the lowest that does is not one.
    private const float LegRootsWithin = 0.12f;
    private const float LegEndsWithin = 0.02f;
    // How fast the height the hips ride at is forgotten, by the pose's height a solve.
    private const float HipSink = 0.0005f;
    // Marks a joint that keeps its angle.
    private const float Locked = -1f;

    private int[] parents = Array.Empty<int>();
    private List<int>[] children = Array.Empty<List<int>>();
    private bool[] structural = Array.Empty<bool>();
    private bool[] core = Array.Empty<bool>();
    private bool[] hanging = Array.Empty<bool>();
    private int[] anchor = Array.Empty<int>();        // nearest structural ancestor-or-self, -1 if none
    private float[] radius = Array.Empty<float>();
    private float[] clearance = Array.Empty<float>();
    private float[] floor = Array.Empty<float>();
    private float[] roof = Array.Empty<float>();
    private Vector3[] solved = Array.Empty<Vector3>();
    private Quaternion[] turned = Array.Empty<Quaternion>();
    private bool[] limbEnd = Array.Empty<bool>();   // the bones of limbs, beyond where they are fixed to the core
    private Quaternion[] heldTurn = Array.Empty<Quaternion>();  // of the bones of a limb that is held up: how they are turned
    private float[] footTurn = Array.Empty<float>();            // of the end of a leg that carries its foot: how far the foot is turned on, kneeling
    private float[] heldUp = Array.Empty<float>();              // and how much it is held up: 0 hangs, 1 held
    private readonly List<int[]> limbs = new();        // each: its bones outward; the first is fixed to the core
    private readonly List<Vector3[]?> folded = new();  // per limb: how it folds under the crouching body, or null if it only hangs
    private readonly List<float> closedUp = new();     // per limb: how far apart its ends still are when it is closed up; 0 if it only hangs
    private readonly List<float> stepping = new();     // per limb: how far the animation has its foot lifted in a step, 0 to 1
    private readonly List<float> drawnUp = new();      // per limb: how far it has the leg drawn up as it steps, 0 to 1, as evenly as it draws it
    private int[] legBones = Array.Empty<int>();       // per bone a limb starts at: how many of its bones made a leg when it last stood
    private long[] legSeen = Array.Empty<long>();      // and at which solve that was
    private float[] hipHigh = Array.Empty<float>();    // per bone a leg starts at: as high as it has been of late
    private long[] hipSeen = Array.Empty<long>();      // and the solve it was last looked at
    private float[] endLow = Array.Empty<float>();     // per bone a leg starts at: as near the ground as its end has been of late
    private long[] endSeen = Array.Empty<long>();
    private float lowestLegEnd;                        // and how near the ground the lowest ends
    private float lowestLegRoot;                       // of the limbs that stand, by the pose: how low the lowest is held
    private float steadyHeight;                        // the tallest the pose has been of late
    private long steadySeen;
    private long solves;
    private float earlyKneel;                          // how far the body is one that goes down on its knees from standing, 0 to 1
    private bool within;                               // solving again, as low as it goes, within a solve
    private float hipRoll;                             // how far the hips are rolled, kneeling: the hip of a knee that steps is up
    private float poseGround;                          // the height the pose stands on, in its own space
    private int pivotBone = -1;
    private float poseHeight = 1f;
    private float risen;                               // how far the ground has come up, this solve
    private Vector3 lastForward = Vector3.UnitZ;

    /// <summary>The bones that take part, after <see cref="Solve"/>; the rest follow them rigidly.</summary>
    public IReadOnlyList<bool> Structural => structural;

    /// <summary>The bones of limbs the body does not stand on (arms, a neck, a tail, cloth), after <see cref="Solve"/>.</summary>
    public IReadOnlyList<bool> Hanging => hanging;

    /// <summary>What the last <see cref="Solve"/> decided for the core: its tip (radians), its lift, and the lifts its legs and the ground alone ask for.</summary>
    public float LastTip { get; private set; }
    public float LastLift { get; private set; }
    public float LastCrouch { get; private set; }
    public float LastResting { get; private set; }

    /// <summary>How far down the body is after the last <see cref="Solve"/>: 0 crouching, 1 on all fours, 2 as low as it goes.</summary>
    public float LastStage { get; private set; }

    /// <summary>
    /// The ground height at which the body is as low as it goes, as of the last <see cref="Solve"/>;
    /// pushing its owner deeper changes nothing. Not a number for a body that only crouches.
    /// </summary>
    public float LastDeepest { get; private set; } = float.NaN;

    /// <summary>The limbs, after <see cref="Solve"/>: each its bones outward from the core.</summary>
    public IReadOnlyList<int[]> Limbs => limbs;

    /// <summary>Whether the body stands on the limb of that index, after <see cref="Solve"/>.</summary>
    public bool StandsOn(int limb) => limb < folded.Count && folded[limb] != null;

    /// <summary>The bones of the core, after <see cref="Solve"/>.</summary>
    public IReadOnlyList<bool> Core => core;

    /// <summary>
    /// Whether this skeleton is one the fit handles well. It is pushed all the way under, a little
    /// at a time, from the given pose, and must come through without a bone left under the ground
    /// and without its body or the limbs it stands on jumping from one step to the next. A creature
    /// that fails is better left to sink into the ground than shown fitted badly.
    /// </summary>
    public static bool Handles(ReadOnlySpan<int> parentIndices, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Quaternion> rotations, Vector3 forward)
    {
        var count = Math.Min(parentIndices.Length, Math.Min(positions.Length, rotations.Length));
        if (count < 2) return false;
        float lowest = float.MaxValue, highest = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            lowest = MathF.Min(lowest, positions[i].Y);
            highest = MathF.Max(highest, positions[i].Y);
        }
        var height = highest - lowest;
        if (!(height > 0.05f)) return false;

        const int Steps = 60;
        var solver = new GroundConformSolver();
        var fitted = new Vector3[count];
        var turned = new Quaternion[count];
        var previous = new Vector3[count];
        var hadPrevious = false;
        var dip = MathF.Min(0f, lowest);
        var step = 1.3f * height / Steps;
        for (var n = 1; n <= Steps; n++)
        {
            positions[..count].CopyTo(fitted);
            rotations[..count].CopyTo(turned);
            var ground = step * n;
            if (!solver.Solve(parentIndices[..count], fitted, turned, ground, forward))
            {
                hadPrevious = false;
                continue;
            }
            for (var i = 0; i < count; i++)
            {
                if (!float.IsFinite(fitted[i].X + fitted[i].Y + fitted[i].Z)) return false;
                if (!solver.structural[i]) continue;
                if (fitted[i].Y < ground + dip - 0.03f * height) return false;
                if (hadPrevious && !solver.hanging[i] && Vector3.Distance(fitted[i], previous[i]) > 12f * step) return false;
            }
            fitted.CopyTo(previous, 0);
            hadPrevious = true;
        }
        return true;
    }

    /// <summary>
    /// Solves one frame.
    /// </summary>
    /// <param name="parentIndices">Parent of each bone, -1 for a root; parents come before children.</param>
    /// <param name="positions">Animated bone positions in the skeleton's space. Replaced by the fitted ones.</param>
    /// <param name="rotations">Animated bone rotations in the same space. Replaced by the fitted ones.</param>
    /// <param name="groundHeight">Height of the ground in that space: how far the owner has been pushed down.</param>
    /// <param name="forward">The way the owner faces, in that space; the way a body tips when it must.</param>
    /// <returns>False when nothing reaches the ground and the pose is left as it is.</returns>
    public bool Solve(ReadOnlySpan<int> parentIndices, Span<Vector3> positions, Span<Quaternion> rotations,
        float groundHeight, Vector3 forward)
    {
        var count = Math.Min(parentIndices.Length, Math.Min(positions.Length, rotations.Length));
        if (count < 2 || !float.IsFinite(groundHeight))
            return false;
        for (var i = 0; i < count; i++)
            if (!float.IsFinite(positions[i].X + positions[i].Y + positions[i].Z))
                return false;
        positions = positions[..count];
        rotations = rotations[..count];

        Prepare(parentIndices[..count], positions);
        if (pivotBone < 0)
            return false;
        risen = MathF.Max(0f, groundHeight);
        // (What is remembered from one solve to the next is not touched by a solve within one.)
        if (!within) solves++;
        lowestLegRoot = float.MaxValue;
        lowestLegEnd = float.MaxValue;
        foreach (var limb in limbs)
        {
            var stands = Leg(limb, positions);
            if (stands == 0) continue;
            lowestLegRoot = MathF.Min(lowestLegRoot, positions[limb[0]].Y);
            lowestLegEnd = MathF.Min(lowestLegEnd, positions[limb[stands - 1]].Y - clearance[limb[stands - 1]]);
        }

        // Nothing to do while the whole body is clear of the ground.
        var engaged = false;
        var top = float.MinValue;
        var thickest = 0f;
        for (var i = 0; i < count; i++)
        {
            if (!structural[i]) continue;
            floor[i] = groundHeight + clearance[i];
            engaged |= positions[i].Y < floor[i] - 1e-5f;
            top = MathF.Max(top, positions[i].Y + radius[i]);
            thickest = MathF.Max(thickest, radius[i]);
        }
        if (!engaged)
            return false;

        forward.Y = 0f;
        forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        lastForward = forward;
        // The ceiling: where the pose's top was, but never so low that the body cannot lie under it.
        var ceiling = MathF.Max(top, groundHeight + 2f * thickest);
        for (var i = 0; i < count; i++)
            if (structural[i])
                roof[i] = MathF.Max(ceiling - radius[i], floor[i]);

        // The core: held where the animation has it, lifted by the ground where it must be, and
        // tipped forward about that hold by the least angle that keeps it under the ceiling.
        var pivot = positions[pivotBone];
        var axis = Vector3.Cross(Vector3.UnitY, forward);

        // The ceiling is never lower than the core can get under at its flattest: a body that is
        // long rather than tall (four legs, a belly) is flattest as it stands, and never tips.
        var flattest = float.MaxValue;
        var flattestAt = 0f;
        for (var step = 0; step <= 45; step++)
        {
            Lift(positions, pivot, axis, MathF.PI / 2f * step / 45, out var tippedTop);
            if (tippedTop >= flattest - 1e-5f) continue;
            flattest = tippedTop;
            flattestAt = MathF.PI / 2f * step / 45;
        }
        var poseTop = top;
        if (flattest + 1e-4f > ceiling)
        {
            ceiling = flattest + 1e-4f;
            for (var i = 0; i < count; i++)
                if (structural[i])
                    roof[i] = MathF.Max(ceiling - radius[i], floor[i]);
        }

        // The legs carry it too. Folded as far as they go, they hold the upright core at that height.
        folded.Clear();
        // How far the core has to tip to be level: by how steeply it stands over the bone it is
        // held by. A core that is level as it stands (four legs under a belly) does not tip, and
        // its legs do not turn.
        var middle = Vector3.Zero;
        for (var i = 0; i < count; i++)
            if (core[i]) middle += positions[i] - pivot;
        var level = Math.Clamp(MathF.Atan2(middle.Y, Vector3.Dot(middle, forward)), 0f, MathF.PI / 2f);
        if (middle.LengthSquared() < 1e-10f) level = 0f;
        var upright = CrouchLift(positions, forward, Lift(positions, pivot, axis, 0f, out _), groundHeight,
            pivot, Quaternion.CreateFromAxisAngle(axis, level), out var past, out var kneelsOver);

        // How each limb the body stands on is folded under the crouching body.
        folded.Clear();
        Span<Vector3> chain = stackalloc Vector3[64];
        for (var l = 0; l < limbs.Count; l++)
        {
            var limb = limbs[l];
            var bones = LegOf(limb, positions);
            if (bones == 0) { folded.Add(null); continue; }
            for (var k = 0; k < bones; k++)
                chain[k] = positions[limb[k]] + new Vector3(0f, upright, 0f);
            var end = limb[bones - 1];
            FitChain(chain[..bones], limb, Planted(limb[0], end, chain[0], closedUp[l], positions), forward);
            LayClear(chain[..bones], limb, forward);
            folded.Add(chain[..bones].ToArray());
        }
        Step(positions);
        hipRoll = 0f;

        // It comes down further only by going down on its front, and how far is how far it is
        // pushed past what its legs allow. First on all fours: the core tips until it is level, and
        // the legs turn from folded under it to kneeling. Then low: the knees slide back.
        var angle = 0f;
        var kneeling = 0f;  // how far it is from crouching on its feet (0) to kneeling (1)
        var lean = 0f;      // how far back its thighs then lean, radians
        var fit = 0f;       // how much further it tips, kneeling, to fit the body
        var stage = 0f;
        var low = 0f;       // how far it is from all fours (0) to as low as it goes (1)
        var deepest = float.NaN;
        var resting = Lift(positions, pivot, axis, 0f, out var standingTop);
        // (A body that goes down on its knees early does so from standing; one that does not, only
        // once its legs are folded as far as they go and hold it up.)
        // (And only a body its legs carry: one whose core is on the ground as it stands is not
        // on its legs.)
        var offGround = float.MaxValue;
        for (var i = 0; i < count; i++)
            if (core[i] || structural[i] && core[parents[i]])
                offGround = MathF.Min(offGround, positions[i].Y - clearance[i]);
        if (past > float.MinValue && (earlyKneel > 0f && offGround > SupportShare * poseHeight || upright > resting + 1e-5f))
        {
            // (A core that stands more upright than not goes all the way down on its knees: one
            // that leans into a run kneels no less than one that walks upright.)
            var share = Smooth(level / (MathF.PI / 4f));

            stage = Math.Clamp(1f + (past < 0f ? past / kneelsOver : past / (StageShare * poseHeight)), 0f, 2f);
            deepest = groundHeight - past + StageShare * poseHeight;
            var down = Smooth(MathF.Min(stage, 1f));
            var lower = Smooth(MathF.Max(stage - 1f, 0f));
            low = lower;
            angle = level * down;
            kneeling = share * down;
            lean = share * LowestLean * lower;

            // As low as it goes, it goes no lower: pushed deeper it is as it was there, and the
            // ground carries it.
            if (share > 0f && groundHeight > deepest + 1e-4f && !within)
            {
                var carried = groundHeight - deepest;
                within = true;
                bool fitted;
                try { fitted = Solve(parentIndices, positions, rotations, deepest, forward); }
                finally { within = false; }
                for (var i = 0; fitted && i < count; i++)
                    if (anchor[i] >= 0) positions[i].Y += carried;
                LastDeepest = deepest;
                return fitted;
            }
            if (share <= 0f) deepest = float.NaN;
            // A body that goes down on its limbs is not squeezed under where its top was: on all
            // fours its hips and shoulders are where its limbs hold them.
            if (share > 0f && stage > 0f)
                for (var i = 0; i < count; i++)
                    if (structural[i]) roof[i] = float.MaxValue;
        }
        // A core that stands tall without legs under it (or taller than they let it lie) goes down
        // on its front as the ground carries it up past where its top was: not at all while it is
        // under that, all the way to its flattest once only its flattest would be.
        if (standingTop > poseTop + 1e-5f && standingTop > flattest + 1e-4f)
            angle = MathF.Max(angle, flattestAt * Math.Clamp((standingTop - poseTop) / (standingTop - flattest), 0f, 1f));
        // (A knee rests nearer the ground than a foot stands on it.)
        if (kneeling > 0f)
            for (var l = 0; l < folded.Count; l++)
            {
                if (folded[l] == null) continue;
                var limb = limbs[l];
                var knee = Knee(limb, folded[l]!.Length, positions);
                for (var k = 1; k <= knee; k++)
                    floor[limb[k]] -= KneeSink * kneeling * (clearance[limb[k]] - poseGround);
            }
        // The core rests on the ground, or on its legs: folded under it while it crouches, and on
        // its knees as it goes down on them. On its knees it is tipped a little further, or less
        // far, to fit the body: until the limbs it stands on in front reach the ground.
        Quaternion tipped;
        Vector3 raise;
        for (var pass = 0; ; pass++)
        {
            tipped = Quaternion.CreateFromAxisAngle(axis, angle);
            var held = upright > resting + 1e-5f ? upright : float.MinValue;
            var sway = 0f;
            hipRoll = 0f;
            if (kneeling > 0f)
            {
                // The knee that steps is lifted, by its hip: the hips roll until every knee is as
                // far off the ground as it should be, as nearly as they can.
                hipRoll = HipRoll(positions, pivot, tipped, axis, kneeling, lean, out sway);
                var onKnees = KneelLift(positions, pivot, tipped, axis, kneeling, lean);
                // (Never lower than its knees let it: they are not pushed into the ground and out again.)
                if (onKnees > float.MinValue) held = MathF.Max(onKnees, upright + (onKnees - upright) * kneeling);
            }
            // (And its weight goes over the knees it stands on.)
            raise = new Vector3(0f, MathF.Max(Lift(positions, pivot, axis, angle, out _), held), 0f) + axis * sway;
            if (kneeling <= 0f || pass >= 1) break;
            var further = FrontFit(positions, pivot, tipped, raise, forward, low) * kneeling;
            var fitted = Math.Clamp(fit + further, -MostFit * kneeling, MostFit * kneeling);
            if (MathF.Abs(fitted - fit) < 1e-4f) break;
            angle += fitted - fit;
            fit = fitted;
        }
        LastStage = stage;
        LastDeepest = deepest;
        LastTip = angle;
        LastLift = raise.Y;
        LastCrouch = upright;
        LastResting = resting;

        // Everything first follows the core rigidly; the limbs are then fitted one by one.
        for (var i = 0; i < count; i++)
            solved[i] = pivot + Vector3.Transform(positions[i] - pivot, tipped) + raise;

        for (var l = 0; l < limbs.Count; l++)
        {
            var limb = limbs[l];
            var bones = Math.Min(limb.Length, chain.Length);
            if (bones < 2) continue;
            var stands = folded[l] != null;
            if (stands)
            {
                // The leg, and the foot beyond where it stands: crouching the foot stays as it
                // stands on the ground; kneeling it is turned back with the shin.
                StandingLimb(l, positions, pivot, tipped, axis, kneeling, lean, chain[..bones]);
                for (var k = 0; k < bones; k++)
                    chain[k] += raise;
                LayClear(chain[..bones], limb, forward);
            }
            else
            {
                // A limb that is held up (a neck, a raised arm) goes where the core takes it, as it
                // is. One that hangs down (an arm at rest, a tail, cloth) hangs down still when the
                // core tips: it keeps the way it points, from where the core now holds it. So the
                // arms of a body going down on its front meet the ground under its shoulders.
                // Whatever of either is then under the floor or over the ceiling is laid along it.
                var reach = positions[limb[bones - 1]] - positions[limb[0]];
                var hangs = reach.LengthSquared() > 1e-10f ? 1f - Smooth(Math.Clamp(reach.Y / reach.Length() / 0.5f, 0f, 1f)) : 0f;
                // A limb that is held up is lifted part of the way back: a neck comes up from the
                // back and the head looks ahead, not at the ground.
                var upheld = 1f - hangs;
                var kept = Quaternion.CreateFromAxisAngle(axis, angle * upheld * (1f - HeadKept));
                chain[0] = solved[limb[0]];
                for (var k = 1; k < bones; k++)
                {
                    chain[k] = chain[k - 1] + Vector3.Transform(positions[limb[k]] - positions[limb[k - 1]], kept);
                    heldTurn[limb[k]] = kept;
                    heldUp[limb[k]] = upheld;
                }
                // A limb that points down at the ground and can fold (an arm under a shoulder) is
                // stood on it, and folds as a leg does; and as a leg does, it reaches less far out
                // from under where it is held the lower that is. The steeper it points, the more
                // it is stood; one that trails (a tail) is laid instead.
                var last = limb[bones - 1];
                var aim = chain[bones - 1];
                var under = floor[last] - aim.Y;
                var along = aim - chain[0];
                if (bones >= 3 && under > 0f && along.Y < 0f)
                {
                    var steep = Smooth((-along.Y / along.Length() - 0.5f) / 0.3f);
                    var share = Math.Clamp((chain[0].Y - floor[last]) / -along.Y, 0f, 1f);
                    aim = Vector3.Lerp(aim, new Vector3(chain[0].X + along.X * share, floor[last], chain[0].Z + along.Z * share), steep);
                }
                // On all fours, a limb in front of the legs that hangs and has the reach is a hand:
                // it is put down on the ground under its shoulder, a little in from it, and as far
                // ahead or behind as the animation swings it, however the animation holds it. And
                // it is lifted while the leg behind it on the other side steps, as the limbs of a
                // body on four step in pairs.
                var front = bones >= 3 ? kneeling * hangs * InFront(limb[0]) : 0f;
                // (It stands on its wrist: where it ends in a short bone that takes part, a finger
                // of a big hand, that lies on the ground ahead of the wrist.)
                var stood = bones - 1;
                var arm = 0f;
                for (var k = 2; k < bones; k++)
                    arm += Vector3.Distance(chain[k], chain[k - 1]);
                var finger = Vector3.Distance(chain[bones - 1], chain[bones - 2]);
                if (bones >= 4 && finger < 0.2f * arm)
                {
                    stood = bones - 2;
                    arm -= finger;
                }
                if (front > 0f)
                {
                    var wrist = limb[stood];
                    var shoulder = chain[1];
                    var height = MathF.Max(0f, shoulder.Y - floor[wrist]);
                    var swung = positions[wrist] - positions[limb[1]];
                    // (By how far ahead of the shoulder the animation has the wrist, for the arm's
                    // length: a hand held up in front of the chest in a run is ahead, not behind.)
                    var swing = MathF.Asin(Math.Clamp(Vector3.Dot(swung, forward) / MathF.Max(1e-6f, arm), -MathF.Sin(MostHandSwing), MathF.Sin(MostHandSwing)));
                    var held = pivot + raise;
                    var outward = Vector3.Dot(shoulder - held, axis);
                    // (No further than the arm reaches from there.)
                    var most = MathF.Sqrt(MathF.Max(0f, 0.98f * arm * arm - height * height));
                    var hand = shoulder + axis * ((HandsApart - 1f) * outward) + forward * Math.Clamp(height * MathF.Tan(swing), -most, most);
                    hand.Y = floor[wrist] + HandLiftShare * poseHeight * SteppingWith(l, positions, pivot, axis);
                    // (Only as far as it has the reach.)
                    var whole = 0f;
                    for (var k = 1; k <= stood; k++)
                        whole += Vector3.Distance(chain[k], chain[k - 1]);
                    front *= Smooth((whole - Vector3.Distance(hand, chain[0])) / (0.15f * whole));
                    if (stood == bones - 1)
                        aim = Vector3.Lerp(aim, hand, front);
                    else if (front > 0f)
                    {
                        var pointed = chain[bones - 1] - chain[stood];
                        FitChain(chain[..(stood + 1)], limb, Vector3.Lerp(chain[stood], hand, front), forward);
                        // The finger: ahead along the ground, as high as it rests.
                        var flat = new Vector3(pointed.X, 0f, pointed.Z) + forward * (0.5f * finger);
                        flat = flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : forward;
                        var rise = Math.Clamp(floor[last] + (hand.Y - floor[wrist]) - chain[stood].Y, -finger, finger);
                        var laid = flat * MathF.Sqrt(MathF.Max(0f, finger * finger - rise * rise)) + new Vector3(0f, rise, 0f);
                        var way = Vector3.Lerp(pointed, laid, front);
                        chain[bones - 1] = chain[stood] + (way.LengthSquared() > 1e-12f ? Vector3.Normalize(way) : forward) * finger;
                        aim = chain[bones - 1];
                    }
                }
                if (Vector3.DistanceSquared(aim, chain[bones - 1]) > 1e-12f)
                    FitChain(chain[..bones], limb, aim, forward);
                // (Its elbow points back and a little out, whichever way the animation had the arm
                // turned: the arm is rolled about the line from shoulder to wrist until it does.)
                if (front > 0f && stood >= 3)
                    ElbowBack(chain[1..(stood + 1)], Vector3.Normalize(axis * (0.5f * MathF.Sign(Vector3.Dot(chain[1] - (pivot + raise), axis))) - forward), front);
                LayClear(chain[..bones], limb, forward);
            }
            hanging[limb[0]] = !stands;
            if (stands) solved[limb[0]] = chain[0];
            for (var k = 1; k < bones; k++)
            {
                solved[limb[k]] = chain[k];
                // (A foot is turned over on the way down to the knees, as feet are; it trails.)
                hanging[limb[k]] = !stands || k >= folded[l]!.Length;
                limbEnd[limb[k]] = true;
            }
            // (A foot too small to take part is carried by the shin, and laid back with it would
            // point its toes into the ground: it is turned on to trail, as one that takes part is.)
            if (stands && folded[l]!.Length == bones)
                footTurn[limb[bones - 1]] = CarriedFootTurn * kneeling;
        }

        Orient(positions, rotations);
        return true;
    }

    /// <summary>
    /// Brings every joint of a limb between floor and ceiling, from its root outward, keeping every
    /// bone's length. A joint that is out is put back in by leaning its bone: the bone keeps the way
    /// it points along the ground (forward, if it points straight up or down) and leans as far as it
    /// must; everything beyond the joint moves with it. Nothing is chosen, so nothing can flip: a
    /// little more push gives a little more lean.
    /// </summary>
    private void LayClear(Span<Vector3> chain, int[] limb, Vector3 forward)
    {
        for (var k = 1; k < chain.Length; k++)
        {
            var held = Math.Clamp(chain[k].Y, floor[limb[k]], roof[limb[k]]);
            if (MathF.Abs(held - chain[k].Y) < 1e-6f) continue;
            var bone = chain[k] - chain[k - 1];
            var length = bone.Length();
            if (length < 1e-7f) continue;
            var rise = Math.Clamp(held - chain[k - 1].Y, -length, length);
            var along = new Vector3(bone.X, 0f, bone.Z) + forward * (0.05f * length);
            var way = along.LengthSquared() > 1e-12f ? Vector3.Normalize(along) : forward;
            var moved = chain[k - 1] + way * MathF.Sqrt(MathF.Max(0f, length * length - rise * rise)) + new Vector3(0f, rise, 0f);
            var shift = moved - chain[k];
            for (var j = k; j < chain.Length; j++)
                chain[j] += shift;
        }
    }

    /// <summary>Eases a share in and out: 0 at 0, 1 at 1, level at both.</summary>
    private static float Smooth(float share)
    {
        share = Math.Clamp(share, 0f, 1f);
        return share * share * (3f - 2f * share);
    }

    /// <summary>
    /// If the body stands on this limb, how many of its bones make the leg (see <see cref="Leg"/>),
    /// or did a moment ago: a foot lifted high in a step is still a foot.
    /// </summary>
    private int LegOf(int[] limb, ReadOnlySpan<Vector3> positions)
    {
        var bones = Leg(limb, positions);
        var root = limb[0];
        // (Not one held far higher than the legs are: an arm that swings low in a run hangs from
        // the shoulder, and is no leg for it.)
        if (bones > 0 && positions[root].Y > lowestLegRoot + LegRootsWithin * poseHeight)
            bones = 0;
        // (Nor one that ends well over the ground the lowest leg stands on: cloth that hangs to
        // the shins is no leg beside the legs.)
        if (bones > 0 && positions[limb[bones - 1]].Y - clearance[limb[bones - 1]] > lowestLegEnd + LegEndsWithin * poseHeight)
            bones = 0;
        // (As much of it as made the leg a moment ago, if that was more: a shin swung up behind in a
        // step is still part of the leg.)
        var remembered = legBones[root] > 0 && solves - legSeen[root] <= LegMemory;
        if (remembered && legBones[root] > limb.Length) legBones[root] = limb.Length;
        if (bones > 0 && (!remembered || bones >= legBones[root]))
        {
            legBones[root] = bones;
            legSeen[root] = solves;
        }
        else if (remembered)
            bones = legBones[root];
        return bones;
    }

    /// <summary>
    /// If the body stands on this limb, by the pose alone, how many of its bones make the leg: down
    /// to the first that is low in the pose, which is where it stands (an ankle, a wrist). What is
    /// beyond that is the foot, and keeps to the ground as the animation has it. Zero for a limb
    /// that only hangs: one that cannot fold, that reaches nowhere near the ground, or that does
    /// not hang down.
    /// </summary>
    private int Leg(int[] limb, ReadOnlySpan<Vector3> positions)
    {
        var bones = Math.Min(limb.Length, 64);
        for (var k = 2; k < bones; k++)
        {
            var bone = limb[k];
            if (positions[bone].Y - clearance[bone] >= SupportShare * poseHeight) continue;
            var length = 0f;
            for (var j = 1; j <= k; j++)
                length += Vector3.Distance(positions[limb[j]], positions[limb[j - 1]]);
            if (!(positions[limb[0]].Y - positions[bone].Y > 0.5f * length)) return 0;
            // (A short leg has its knee that low too, well off the ground: the leg then goes on
            // down the next bone, if that goes steeply down from there. A foot does not.)
            var end = k;
            if (end + 1 < bones && positions[bone].Y - clearance[bone] > 0.5f * SupportShare * poseHeight)
            {
                var next = positions[limb[end + 1]] - positions[limb[end]];
                if (-next.Y > 0.7f * next.Length()) end++;
            }
            return end + 1;
        }
        return 0;
    }

    /// <summary>
    /// A limb the body stands on, for the given tip of the core, before any lift: every bone points
    /// part of the way from where the crouch has it to where it is when the body kneels. Kneeling,
    /// the thigh points the way the whole leg does in the animation (it hangs from the hip, and
    /// swings as far as the leg is swung: the knee steps where the foot stepped), leant back by the
    /// given angle; the rest of the leg lies back behind the knee, a little off the ground and
    /// more as it steps, and the foot trails from it, toes down.
    /// </summary>
    private void StandingLimb(int index, ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 axis, float kneeling, float lean, Span<Vector3> into)
    {
        var limb = limbs[index];
        var crouched = folded[index]!;
        var knee = Knee(limb, crouched.Length, positions);
        var leant = Quaternion.CreateFromAxisAngle(axis, lean);
        var laid = Quaternion.CreateFromAxisAngle(axis, MathF.PI / 2f);
        var trailing = Quaternion.CreateFromAxisAngle(axis, KneelingFootTurn);
        // The hip, where the core holds it: rolled about the bone that carries it.
        var carrier = parents[limb[0]];
        into[0] = pivot + Vector3.Transform(positions[carrier] - pivot, tipped)
                  + Vector3.Transform(Vector3.Transform(positions[limb[0]] - positions[carrier], tipped), Quaternion.CreateFromAxisAngle(lastForward, hipRoll));
        // The way the whole leg points, swinging less the further the thigh leans back.
        var whole = positions[limb[crouched.Length - 1]] - positions[limb[0]];
        var swings = MathF.Sin(lean) / LeanAtHalfSwing;
        swings = 1f / (1f + swings * swings);
        var swung = whole.Y < -1e-6f ? Vector3.Normalize(Vector3.Lerp(-Vector3.UnitY, Vector3.Normalize(whole), swings * KneelingSwing)) : -Vector3.UnitY;
        // (Kneeling, a leg at the body's side stands a little out to that side, however the
        // animation has it: the feet of a walk are under the body's middle, the knees of a crawl
        // are not.)
        var side = Math.Clamp(Vector3.Dot(positions[limb[0]] - pivot, axis) / (0.03f * poseHeight), -1f, 1f);
        swung = Vector3.Normalize(swung + axis * (side * ThighsApart));
        var legLength = 0f;
        for (var k = 1; k < crouched.Length; k++)
            legLength += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
        for (var k = 1; k < into.Length; k++)
        {
            var bone = positions[limb[k]] - positions[limb[k - 1]];
            var length = bone.Length();
            // (Beyond where the leg stands, the foot: crouching, as it is animated.)
            var from = k < crouched.Length ? crouched[k] - crouched[k - 1] : bone;
            if (length < 1e-7f || from.LengthSquared() < 1e-14f)
            {
                into[k] = into[k - 1] + Vector3.Transform(bone, tipped);
                continue;
            }
            from = Vector3.Normalize(from);
            Vector3 kneels;
            if (k <= knee)
                kneels = Vector3.Transform(swung, leant);
            else if (k < crouched.Length)
            {
                // Laid straight back, as far to the side as it points and a little further out to
                // the side its leg is on.
                var aside = Math.Clamp(Vector3.Dot(bone, axis) / length + side * ShinsApart, -0.9f, 0.9f);
                kneels = axis * aside - lastForward * MathF.Sqrt(1f - aside * aside);
            }
            else
                kneels = Vector3.Transform(bone / length, trailing);
            into[k] = into[k - 1] + Under(from, kneels, axis, kneeling) * length;
        }
        // The shin, up off the ground behind the knee: what is beyond the knee, as far as its first
        // long bone, is turned up about the knee; what is beyond that is carried.
        var raised = Quaternion.CreateFromAxisAngle(axis, (ShinRest + ShinStep * drawnUp[index]) * kneeling);
        for (var k = knee + 1; k < crouched.Length; k++)
        {
            var bone = into[k] - into[k - 1];
            var moved = Vector3.Transform(bone, raised) - bone;
            for (var j = k; j < into.Length; j++)
                into[j] += moved;
            if (bone.Length() >= ShortBoneShare * legLength) break;
        }
    }

    /// <summary>
    /// Which of the legs the animation is stepping with, and how far, 0 to 1: one that is drawn up
    /// (the leg a walk swings forward is bent, and shorter from hip to foot than the one it
    /// stands on) with its foot off the ground. Both are counted from the leg that is least so:
    /// a body that stands with its knees bent is not stepping.
    /// </summary>
    private void Step(ReadOnlySpan<Vector3> positions)
    {
        stepping.Clear();
        drawnUp.Clear();
        Span<float> drawn = stackalloc float[limbs.Count];
        Span<float> off = stackalloc float[limbs.Count];
        var least = float.MaxValue;
        var lowest = float.MaxValue;
        for (var l = 0; l < limbs.Count; l++)
        {
            if (folded[l] == null) continue;
            var limb = limbs[l];
            var bones = folded[l]!.Length;
            var straight = 0f;
            for (var k = 1; k < bones; k++)
                straight += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
            drawn[l] = straight > 1e-6f ? 1f - Vector3.Distance(positions[limb[bones - 1]], positions[limb[0]]) / straight : 0f;
            least = MathF.Min(least, drawn[l]);
            off[l] = MathF.Max(0f, positions[limb[bones - 1]].Y - clearance[limb[bones - 1]]);
            lowest = MathF.Min(lowest, off[l]);
        }
        for (var l = 0; l < limbs.Count; l++)
        {
            var offGround = folded[l] == null ? 0f : Smooth((off[l] - lowest) / (FootOffShare * poseHeight));
            stepping.Add(offGround * Smooth((drawn[l] - least) / DrawnUpShare));
            drawnUp.Add(offGround * Math.Clamp((drawn[l] - least) / (2f * DrawnUpShare), 0f, 1f));
        }
    }

    /// <summary>
    /// A direction part of the way from one to another, turning the way the body tips: from
    /// pointing forward to pointing back by way of pointing down, never up over the body. So a
    /// bone that has half a turn to make does not go one way now and the other a moment later:
    /// it only changes its way where it would point straight up, and no leg does.
    /// </summary>
    private Vector3 Under(Vector3 from, Vector3 to, Vector3 axis, float share)
    {
        var fromAhead = Vector3.Dot(from, lastForward);
        var toAhead = Vector3.Dot(to, lastForward);
        if (fromAhead * fromAhead + from.Y * from.Y < 0.04f || toAhead * toAhead + to.Y * to.Y < 0.04f)
        {
            // (One that points sideways has no forward or back to turn through.)
            var hinge = Vector3.Cross(from, to);
            var turn = MathF.Atan2(hinge.Length(), Vector3.Dot(from, to));
            hinge = hinge.LengthSquared() > 1e-10f ? Vector3.Normalize(hinge) : axis;
            return Vector3.Transform(from, Quaternion.CreateFromAxisAngle(hinge, turn * share));
        }
        // Round the body's side, from straight up: forward a quarter turn, down a half, back three quarters.
        var fromRound = MathF.Atan2(fromAhead, from.Y);
        var toRound = MathF.Atan2(toAhead, to.Y);
        if (fromRound < 0f) fromRound += 2f * MathF.PI;
        if (toRound < 0f) toRound += 2f * MathF.PI;
        var round = toRound - fromRound;
        // (And what is left, out to the side, the short way.)
        var rest = Between(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(axis, round)), to);
        return Vector3.Transform(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(axis, round * share)), Quaternion.Slerp(Quaternion.Identity, rest, share));
    }

    /// <summary>
    /// Rolls the joints between the two ends of a limb about the line through its ends, by a share
    /// of what brings the one furthest from that line to the side given.
    /// </summary>
    private static void ElbowBack(Span<Vector3> arm, Vector3 side, float share)
    {
        var line = arm[^1] - arm[0];
        if (line.LengthSquared() < 1e-10f) return;
        line = Vector3.Normalize(line);
        var furthest = Vector3.Zero;
        for (var k = 1; k < arm.Length - 1; k++)
        {
            var off = arm[k] - arm[0];
            off -= line * Vector3.Dot(off, line);
            if (off.LengthSquared() > furthest.LengthSquared()) furthest = off;
        }
        var wanted = side - line * Vector3.Dot(side, line);
        if (furthest.LengthSquared() < 1e-10f || wanted.LengthSquared() < 1e-6f) return;
        var from = Vector3.Normalize(furthest);
        var to = Vector3.Normalize(wanted);
        var angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), line), Vector3.Dot(from, to));
        var roll = Quaternion.CreateFromAxisAngle(line, angle * share);
        for (var k = 1; k < arm.Length - 1; k++)
            arm[k] = arm[0] + Vector3.Transform(arm[k] - arm[0], roll);
    }

    /// <summary>
    /// How far a limb is in front of the legs, 0 to 1, by where the core now holds it: an arm of a
    /// body that has gone down on its front is, cloth at its hips is not.
    /// </summary>
    private float InFront(int root)
    {
        float ahead = 0f;
        var legs = 0;
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            ahead += Vector3.Dot(solved[root] - solved[limbs[l][0]], lastForward);
            legs++;
        }
        return legs > 0 ? Smooth(ahead / legs / (0.2f * poseHeight)) : 0f;
    }

    /// <summary>
    /// How much further the core has to tip, kneeling, for the limbs it stands on in front to
    /// reach the ground from where it holds them: by a share of their length that leaves them a
    /// little bent, less as it goes low. Negative when they are too long for where it has them.
    /// </summary>
    private float FrontFit(ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 raise, Vector3 forward, float low)
    {
        // Where the legs are held, to tell what is in front of them.
        float legsAhead = 0f;
        var legs = 0;
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            legsAhead += Vector3.Dot(Vector3.Transform(positions[limbs[l][0]] - pivot, tipped), forward);
            legs++;
        }
        if (legs == 0) return 0f;
        legsAhead /= legs;

        float turn = 0f, weight = 0f;
        for (var l = 0; l < limbs.Count; l++)
        {
            var limb = limbs[l];
            if (folded[l] != null || limb.Length < 3) continue;
            var end = limb[^1];
            var reach = positions[end] - positions[limb[0]];
            if (reach.LengthSquared() < 1e-10f) continue;
            var hangs = 1f - Smooth(Math.Clamp(reach.Y / reach.Length() / 0.5f, 0f, 1f));
            // (Where it is held: a limb that hangs keeps the way it points from where the core has it.)
            var held = Vector3.Transform(positions[limb[0]] - pivot, tipped) + positions[limb[1]] - positions[limb[0]];
            var lever = Vector3.Dot(held, forward);
            var front = hangs * Smooth((Vector3.Dot(Vector3.Transform(positions[limb[0]] - pivot, tipped), forward) - legsAhead) / (0.2f * poseHeight));
            // (Only one held well ahead of where the core turns: nearer, tipping does not move it.)
            front *= Smooth((lever - 0.05f * poseHeight) / (0.1f * poseHeight));
            if (front <= 0f) continue;
            var length = 0f;
            for (var k = 2; k < limb.Length; k++)
                length += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
            // (It stands on its wrist: a finger that takes part is not part of its reach.)
            var finger = Vector3.Distance(positions[end], positions[limb[^2]]);
            if (limb.Length >= 4 && finger < 0.2f * length)
            {
                length -= finger;
                end = limb[^2];
            }
            var wanted = (ReachKneeling + (ReachLow - ReachKneeling) * low) * length;
            var over = pivot.Y + held.Y + raise.Y - floor[end] - wanted;
            turn += front * length * Math.Clamp(over / MathF.Max(lever, 0.1f * poseHeight), -MostFit, MostFit);
            weight += front * length;
        }
        return weight > 1e-8f ? turn / weight : 0f;
    }

    /// <summary>
    /// How far a limb that is stood on the ground in front is stepping: as far as the legs behind
    /// it on the other side of the body are, for the limbs of a body on four step in pairs across it.
    /// </summary>
    private float SteppingWith(int index, ReadOnlySpan<Vector3> positions, Vector3 pivot, Vector3 axis)
    {
        var root = limbs[index][0];
        var side = Vector3.Dot(positions[root] - pivot, axis);
        float sum = 0f, weight = 0f;
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            var across = MathF.Max(0f, -side * Vector3.Dot(positions[limbs[l][0]] - pivot, axis));
            sum += across * stepping[l] * Smooth(Vector3.Dot(solved[root] - solved[limbs[l][0]], lastForward) / (0.2f * poseHeight));
            weight += across;
        }
        return weight > 1e-10f ? sum / weight : 0f;
    }

    /// <summary>
    /// How far the hips roll, kneeling, so that each knee is as far off the ground as it should be:
    /// on it, or lifted if it steps. And how far the body shifts sideways over the knees it stands on.
    /// </summary>
    private float HipRoll(ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 axis, float kneeling, float lean, out float sway)
    {
        sway = 0f;
        hipRoll = 0f;
        Span<Vector3> chain = stackalloc Vector3[64];
        Span<float> aside = stackalloc float[folded.Count];
        Span<float> need = stackalloc float[folded.Count];
        Span<float> high = stackalloc float[folded.Count];
        float meanOut = 0f, meanNeed = 0f, standing = 0f, stoodOut = 0f, trailing = 0f, trailedOut = 0f;
        var legs = 0;
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            var bones = folded[l]!.Length;
            StandingLimb(l, positions, pivot, tipped, axis, kneeling, lean, chain[..bones]);
            aside[l] = Vector3.Dot(chain[0] - pivot, axis);
            high[l] = chain[0].Y;
            var lifted = KneeLifted(l, positions, chain[..bones]);
            need[l] = KneeUnder(l, positions, chain[..bones]) + KneeLiftShare * poseHeight * kneeling * lifted;
            meanOut += aside[l];
            meanNeed += need[l];
            stoodOut += aside[l] * (1f - 0.95f * lifted);
            standing += 1f - 0.95f * lifted;
            // (Of two knees it stands on, its weight is over the one behind: it has yet to go
            // over the one it has put forward.)
            var knee = Knee(limbs[l], bones, positions);
            var thigh = chain[knee] - chain[0];
            var behind = 1f - 0.9f * Math.Clamp(2f * Vector3.Dot(thigh, lastForward) / MathF.Max(1e-6f, thigh.Length()), -1f, 1f);
            trailedOut += aside[l] * behind;
            trailing += behind;
            legs++;
        }
        if (legs < 2) return 0f;
        meanOut /= legs;
        meanNeed /= legs;
        sway = SwayShare * kneeling * 0.5f * (stoodOut / standing + trailedOut / trailing - 2f * meanOut);
        // (No further over, in all, than hips go: with what the animation has them rolled already.)
        float spread = 0f, slope = 0f, rolled = 0f;
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            spread += (aside[l] - meanOut) * (aside[l] - meanOut);
            slope += (aside[l] - meanOut) * (need[l] - meanNeed);
            rolled += (aside[l] - meanOut) * high[l];
        }
        if (spread < 1e-8f) return 0f;
        var most = MathF.Sin(MostHipRoll);
        rolled = Math.Clamp(rolled / spread, -1f, 1f);
        return MathF.Asin(Math.Clamp(rolled + slope / spread * kneeling, -most, most)) - MathF.Asin(Math.Clamp(rolled, -most, most));
    }

    /// <summary>
    /// How far the knee of a leg, placed so, is lifted off the ground, 0 to 1: as far as the leg is
    /// stepping, and all the way while the thigh reaches forward: a body on its knees stands on
    /// the knee behind, and the knee ahead comes down as the body comes over it.
    /// </summary>
    private float KneeLifted(int index, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> chain)
    {
        var thigh = chain[Knee(limbs[index], chain.Length, positions)] - chain[0];
        var ahead = Vector3.Dot(thigh, lastForward) / MathF.Max(1e-6f, thigh.Length());
        return MathF.Max(stepping[index], Smooth((ahead - ReachingFrom) / (ReachingFully - ReachingFrom)));
    }

    /// <summary>How far a leg, placed so, is under where it rests on the ground: its knee, kneeling.</summary>
    private float KneeUnder(int index, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> chain)
    {
        // (Or any of the leg beyond it: on the way down to its knees a body is still on its feet.)
        var limb = limbs[index];
        var under = float.MinValue;
        for (var k = 1; k < chain.Length; k++)
            under = MathF.Max(under, floor[limb[k]] - chain[k].Y);
        return under;
    }

    /// <summary>
    /// Which joint of a leg is its knee: its first, not counting a very short bone at the hip. A
    /// knee made of two bones a few centimetres apart bends at the further one: the short bone
    /// between them goes with the thigh.
    /// </summary>
    private static int Knee(int[] limb, int bones, ReadOnlySpan<Vector3> positions)
    {
        var length = 0f;
        for (var k = 1; k < bones; k++)
            length += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
        var knee = 1;
        while (knee < bones - 1 && Vector3.Distance(positions[limb[knee]], positions[limb[0]]) < ShortBoneShare * length)
            knee++;
        while (knee < bones - 2 && Vector3.Distance(positions[limb[knee + 1]], positions[limb[knee]]) < ShortBoneShare * length)
            knee++;
        return knee;
    }

    /// <summary>
    /// How far the ground lifts the core when it is tipped by the given angle, and where its top
    /// then is.
    /// </summary>
    private float Lift(ReadOnlySpan<Vector3> positions, Vector3 pivot, Vector3 axis, float angle, out float coreTop)
    {
        var tip = Quaternion.CreateFromAxisAngle(axis, angle);
        var lift = 0f;
        coreTop = float.MinValue;
        for (var i = 0; i < positions.Length; i++)
        {
            // The core, and where its limbs are fixed to it.
            if (!core[i] && !(structural[i] && core[parents[i]])) continue;
            var y = (pivot + Vector3.Transform(positions[i] - pivot, tip)).Y;
            lift = MathF.Max(lift, floor[i] - y);
            if (core[i]) coreTop = MathF.Max(coreTop, y + radius[i]);
        }
        coreTop += lift;
        return lift;
    }

    /// <summary>
    /// The lift at which the core, tipped so, has the knee of every leg it stands on on the ground
    /// or over it, and those that step as far over it as they are lifted.
    /// </summary>
    private float KneelLift(ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 axis, float kneeling, float lean)
    {
        var lift = float.MinValue;
        // (The knee that is lifted least is on the ground; the others are lifted by as much more.)
        var least = float.MaxValue;
        Span<Vector3> chain = stackalloc Vector3[64];
        Span<float> under = stackalloc float[folded.Count];
        Span<float> lifted = stackalloc float[folded.Count];
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            var bones = folded[l]!.Length;
            StandingLimb(l, positions, pivot, tipped, axis, kneeling, lean, chain[..bones]);
            under[l] = KneeUnder(l, positions, chain[..bones]);
            lifted[l] = KneeLifted(l, positions, chain[..bones]);
            least = MathF.Min(least, lifted[l]);
        }
        for (var l = 0; l < folded.Count; l++)
            if (folded[l] != null)
                lift = MathF.Max(lift, under[l] + KneeLiftShare * poseHeight * kneeling * (lifted[l] - least));
        return lift;
    }

    /// <summary>
    /// How low a creature can crouch: the least lift of the upright core at which every leg it
    /// stands on still fits between its hip and where it stands. A leg folds only so far; closed
    /// up, its two ends are still some distance apart, and the hip cannot come nearer the foot than
    /// that, nor so the ground it stands on. The push beyond it is what the legs hold the body up by.
    /// </summary>
    /// <param name="pivot">The bone the core is held by, and <paramref name="level"/> the turn that brings the core level about it.</param>
    /// <param name="past">How far the ground has risen past where the hips are at the height they kneel at; negative before, least when there are no legs.</param>
    /// <param name="kneelsOver">The rise of the ground over which the body goes from standing down on its knees.</param>
    private float CrouchLift(ReadOnlySpan<Vector3> positions, Vector3 forward, float least, float most, Vector3 pivot, Quaternion level, out float past, out float kneelsOver)
    {
        var lift = least;
        past = float.MinValue;
        kneelsOver = StageShare * poseHeight;
        earlyKneel = 0f;
        var earliest = 0f;
        var furthest = float.MinValue;
        closedUp.Clear();
        Span<Vector3> chain = stackalloc Vector3[64];
        foreach (var limb in limbs)
        {
            var bones = LegOf(limb, positions);
            closedUp.Add(0f);
            if (bones == 0) continue;
            for (var k = 0; k < bones; k++)
                chain[k] = positions[limb[k]];
            // Closed up as far as it goes: aimed at its own root.
            FitChain(chain[..bones], limb, chain[0], forward);
            var closed = Vector3.Distance(chain[bones - 1], chain[0]) * 1.05f;

            var root = positions[limb[0]];
            // (Counted from a leg standing under its hip: a foot lifted or swung in a step does not
            // make the body any less far down.)
            closedUp[^1] = closed;
            var over = risen + clearance[limb[bones - 1]] + closed - root.Y;
            lift = MathF.Max(lift, over);
            // (And counted from as high as the hip rides: a step has the body bob, and it is no
            // further down for that. The height is remembered, and forgotten slowly.)
            var hip = limb[0];
            if (hipSeen[hip] != solves)
            {
                hipHigh[hip] = hipSeen[hip] == solves - 1 ? MathF.Max(root.Y, hipHigh[hip] - HipSink * poseHeight) : root.Y;
                hipSeen[hip] = solves;
            }
            // Kneeling, the hip is a thigh's length over the knee, and the knee on the ground;
            // and the core, level, holds the hip higher or lower than it did upright.
            var knee = Knee(limb, bones, positions);
            var thigh = 0f;
            for (var k = 1; k <= knee; k++)
                thigh += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
            var kneeling = poseGround + (clearance[limb[knee]] - poseGround) * (1f - KneeSink) + thigh;
            var held = hipHigh[hip] + Vector3.Transform(root - pivot, level).Y - (root.Y - pivot.Y);
            // (Counted from where the leg comes to stand on the ground: one that hangs clear of
            // it has the body no way down before that.)
            var clearNow = MathF.Max(0f, positions[limb[bones - 1]].Y - clearance[limb[bones - 1]]);
            if (endSeen[hip] != solves)
            {
                endLow[hip] = endSeen[hip] == solves - 1 ? MathF.Min(clearNow, endLow[hip] + HipSink * poseHeight) : clearNow;
                endSeen[hip] = solves;
            }
            var clear = endLow[hip];
            var beyond = risen + kneeling - held;
            var window = (1f - CrouchShare) * (held - kneeling - clear);
            // (A body whose hips are no higher over the ground than its thighs are long has no
            // way down on its knees from where it stands: it goes down as far as its legs fold
            // first, and on from there.)
            var early = Smooth((held - kneeling - clear) / (0.2f * StageShare * poseHeight) - 4f);
            beyond = (over + root.Y - hipHigh[hip] - StageShare * poseHeight) * (1f - early) + beyond * early;
            window = StageShare * poseHeight * (1f - early) + window * early;
            earliest = MathF.Max(earliest, early);
            var progress = beyond < 0f ? beyond / window : beyond / (StageShare * poseHeight);
            if (progress > furthest)
            {
                furthest = progress;
                past = beyond;
                kneelsOver = window;
            }
        }
        earlyKneel = earliest;
        return MathF.Min(lift, MathF.Max(least, most));
    }

    /// <summary>
    /// Where the end of a limb the body stands on belongs: as high above the risen ground as the
    /// animation has it above the ground it stood on (an ankle is not the sole of a foot: it stands
    /// a hand's breadth up, and still does), and as far out from under its hip as the animation has
    /// it, for the height the hip is now at: a body that is lower takes shorter steps, its legs
    /// swinging as far as they did. Nor does it lift a foot nearer its hip than the leg closes up.
    /// </summary>
    private Vector3 Planted(int hip, int end, Vector3 hipNow, float closed, ReadOnlySpan<Vector3> positions)
    {
        var height = MathF.Max(MathF.Min(positions[end].Y + risen, hipNow.Y - closed), risen + clearance[end]);
        var stood = positions[hip].Y - positions[end].Y;
        var share = stood > 1e-5f ? Math.Clamp((hipNow.Y - height) / stood, 0f, 1f) : 1f;
        return new Vector3(
            hipNow.X + (positions[end].X - positions[hip].X) * share,
            height,
            hipNow.Z + (positions[end].Z - positions[hip].Z) * share);
    }

    /// <summary>
    /// Fits one limb to a target for its end: its first point stays and every bone keeps its length.
    ///
    /// The limb curls or uncurls as a whole: every joint bends further the way it already bends (a
    /// straight joint bends so that it goes forward), all by the same share of what they have left,
    /// until the end is as far from the root as the target is. Then the whole limb is turned to
    /// point at the target, and rolled about that line by the least that keeps its joints between
    /// floor and ceiling. One answer for every target, and near targets give near answers: a knee
    /// never snaps from one side to the other.
    /// </summary>
    private void FitChain(Span<Vector3> chain, int[] limb, Vector3 target, Vector3 forward)
    {
        var last = chain.Length - 1;
        var total = 0f;
        for (var k = 1; k <= last; k++)
            total += Vector3.Distance(chain[k], chain[k - 1]);
        if (total < 1e-5f)
            return;

        // A very short bone is not a joint of its own: a knee made of two bones a few centimetres
        // apart bends as one. The limb is fitted by its long bones, and a short one keeps its place
        // between the two it lies between.
        Span<int> kept = stackalloc int[chain.Length];
        var joints = 0;
        kept[joints++] = 0;
        for (var k = 1; k < last; k++)
            if (Vector3.Distance(chain[k], chain[kept[joints - 1]]) >= ShortBoneShare * total)
                kept[joints++] = k;
        kept[joints++] = last;

        Span<Vector3> reduced = stackalloc Vector3[joints];
        Span<Vector3> before = stackalloc Vector3[chain.Length];
        Span<float> lows = stackalloc float[joints];
        Span<float> highs = stackalloc float[joints];
        chain.CopyTo(before);
        for (var k = 0; k < joints; k++)
        {
            reduced[k] = chain[kept[k]];
            lows[k] = floor[limb[kept[k]]];
            highs[k] = roof[limb[kept[k]]];
        }
        FitJoints(reduced, lows, highs, target, forward);
        for (var k = 1; k < joints; k++)
        {
            int from = kept[k - 1], to = kept[k];
            chain[to] = reduced[k];
            var was = before[to] - before[from];
            var now = reduced[k] - reduced[k - 1];
            if (to - from < 2 || was.LengthSquared() < 1e-12f || now.LengthSquared() < 1e-12f) continue;
            var turn = Between(Vector3.Normalize(was), Vector3.Normalize(now));
            for (var between = from + 1; between < to; between++)
                chain[between] = reduced[k - 1] + Vector3.Transform(before[between] - before[from], turn);
        }
    }

    private static void FitJoints(Span<Vector3> chain, ReadOnlySpan<float> lows, ReadOnlySpan<float> highs, Vector3 target, Vector3 forward)
    {
        var last = chain.Length - 1;
        var needsFit = Vector3.Distance(chain[last], target) > 1e-5f;
        for (var k = 1; k <= last && !needsFit; k++)
            needsFit = chain[k].Y < lows[k] - 1e-5f || chain[k].Y > highs[k] + 1e-5f;
        if (!needsFit)
            return;

        var root = chain[0];
        Span<Vector3> segments = stackalloc Vector3[chain.Length];
        Span<Vector3> axes = stackalloc Vector3[chain.Length];
        Span<float> bends = stackalloc float[chain.Length];
        var total = 0f;
        for (var k = 1; k <= last; k++)
        {
            segments[k] = chain[k] - chain[k - 1];
            total += segments[k].Length();
        }
        if (total < 1e-5f)
            return;
        for (var k = 1; k < last; k++)
        {
            var a = segments[k];
            var b = segments[k + 1];
            // A joint at the far end of a very short bone is part of the joint before it (a knee
            // made of two bones a few centimetres apart): it keeps its angle, and the other bends.
            if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f)
            {
                axes[k] = Vector3.UnitX;
                bends[k] = Locked;
                continue;
            }
            var across = Vector3.Cross(a, b);
            var straight = across.Length() < 0.05f * a.Length() * b.Length();
            // A straight joint bends so that the joint itself goes forward: its far bone swings back.
            // A bone that points forward itself has no forward to give to; its joint goes up instead.
            if (straight)
            {
                var along = Vector3.Normalize(a);
                var ahead = forward - along * Vector3.Dot(forward, along);
                var up = Vector3.UnitY - along * Vector3.Dot(Vector3.UnitY, along);
                var prefer = ahead * ahead.Length() + up * (1f - ahead.Length());
                across = Vector3.Cross(a, -prefer);
            }
            if (across.LengthSquared() < 1e-12f) across = Vector3.Cross(a, Vector3.UnitX);
            if (across.LengthSquared() < 1e-12f) across = Vector3.Cross(a, Vector3.UnitY);
            axes[k] = Vector3.Normalize(across);
            bends[k] = straight ? 0f : MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));
        }

        // Which way each joint closes the limb: bent a touch further on its own, does the end come nearer?
        Span<float> senses = stackalloc float[chain.Length];
        Span<float> probe = stackalloc float[chain.Length];
        Span<Vector3> probed = stackalloc Vector3[chain.Length];
        for (var k = 1; k < last; k++)
        {
            senses[k] = 1f;
            if (bends[k] == Locked) continue;
            bends.CopyTo(probe);
            probe[k] = bends[k] + 0.05f;
            Bent(segments, axes, bends, probe, root, probed);
            var further = Vector3.Distance(probed[last], root);
            probe[k] = MathF.Max(0f, bends[k] - 0.05f);
            Bent(segments, axes, bends, probe, root, probed);
            if (further > Vector3.Distance(probed[last], root) + 1e-6f) senses[k] = -1f;
        }

        // -1 is the limb straightened out, 0 as it is, 1 folded up. Folded just enough, its end is
        // as far from its root as the target is. Of the folds that do that, the one nearest to how
        // the limb is now: found by folding (or unfolding) from where it is, a little at a time.
        Span<Vector3> trial = stackalloc Vector3[chain.Length];
        var wanted = Vector3.Distance(root, target);
        Curl(segments, axes, bends, senses, 0f, root, trial);
        var fold = wanted < Vector3.Distance(trial[last], root) ? 1f : -1f;
        const int Marches = 32;
        float near = 0f, far = fold;
        var crossed = false;
        for (var step = 1; step <= Marches; step++)
        {
            var candidate = fold * step / Marches;
            Curl(segments, axes, bends, senses, candidate, root, trial);
            var distance = Vector3.Distance(trial[last], root);
            if (fold > 0f ? distance <= wanted : distance >= wanted)
            {
                far = candidate;
                crossed = true;
                break;
            }
            near = candidate;
        }
        var curl = far;
        if (crossed)
        {
            for (var step = 0; step < 20; step++)
            {
                curl = (near + far) * 0.5f;
                Curl(segments, axes, bends, senses, curl, root, trial);
                var distance = Vector3.Distance(trial[last], root);
                if (fold > 0f ? distance <= wanted : distance >= wanted) far = curl; else near = curl;
            }
            curl = far;
        }
        Place(segments, axes, bends, senses, curl, root, target, forward, trial);
        for (var k = 1; k <= last; k++)
            chain[k] = trial[k];
    }

    /// <summary>
    /// The limb folded by the given share and turned to its target. When it is then longer than the
    /// target is far, its end keeps the target's height and goes out along the ground: away from the
    /// root the way the target lies, or backward when the target is straight below.
    /// </summary>
    private static void Place(ReadOnlySpan<Vector3> segments, ReadOnlySpan<Vector3> axes, ReadOnlySpan<float> bends, ReadOnlySpan<float> senses,
        float curl, Vector3 root, Vector3 target, Vector3 forward, Span<Vector3> result)
    {
        Curl(segments, axes, bends, senses, curl, root, result);
        var last = segments.Length - 1;
        var reach = result[last] - root;
        var length = reach.Length();
        var aim = target - root;
        if (length > aim.Length() + 1e-6f)
        {
            var rise = Math.Clamp(aim.Y, -length, length);
            var flat = new Vector3(aim.X, 0f, aim.Z);
            var way = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : -forward;
            aim = way * MathF.Sqrt(MathF.Max(0f, length * length - rise * rise)) + new Vector3(0f, rise, 0f);
        }
        if (length < 1e-6f || aim.LengthSquared() < 1e-12f)
            return;
        var turn = Between(reach / length, Vector3.Normalize(aim));
        for (var k = 1; k <= last; k++)
            result[k] = root + Vector3.Transform(result[k] - root, turn);
    }

    /// <summary>The limb with every joint bent by the same share: -1 straight, 0 as it is, 1 folded up.</summary>
    private static void Curl(ReadOnlySpan<Vector3> segments, ReadOnlySpan<Vector3> axes, ReadOnlySpan<float> bends, ReadOnlySpan<float> senses,
        float curl, Vector3 root, Span<Vector3> result)
    {
        Span<float> now = stackalloc float[segments.Length];
        for (var k = 1; k < segments.Length - 1; k++)
        {
            if (bends[k] == Locked) { now[k] = Locked; continue; }
            // Folding up, a joint goes the way that brings the limb's ends together: for most that
            // is bending further, for the odd joint of a zigzag leg it is straightening.
            var closing = curl * senses[k];
            now[k] = closing >= 0f ? bends[k] + closing * MathF.Max(0f, Folded - bends[k]) : bends[k] * (1f + closing);
        }
        Bent(segments, axes, bends, now, root, result);
    }

    private const float Folded = 2.7f; // radians: bones do not lie on top of each other

    /// <summary>The limb with its joints at the given bends.</summary>
    private static void Bent(ReadOnlySpan<Vector3> segments, ReadOnlySpan<Vector3> axes, ReadOnlySpan<float> bends, ReadOnlySpan<float> now,
        Vector3 root, Span<Vector3> result)
    {
        var last = segments.Length - 1;
        var frame = Quaternion.Identity;
        result[0] = root;
        for (var k = 1; k <= last; k++)
        {
            result[k] = result[k - 1] + Vector3.Transform(segments[k], frame);
            if (k == last) break;
            if (bends[k] == Locked) continue;
            var axis = Vector3.Transform(axes[k], frame);
            frame = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, now[k] - bends[k]) * frame);
        }
    }

    /// <summary>How far the limb's joints are below their floor or above their ceiling, in all.</summary>
    private static float Outside(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> lows, ReadOnlySpan<float> highs)
    {
        var outside = 0f;
        for (var k = 1; k < points.Length; k++)
        {
            outside += MathF.Max(0f, lows[k] - points[k].Y);
            outside += MathF.Max(0f, points[k].Y - highs[k]);
        }
        return outside;
    }

    /// <summary>The shortest turn from one direction to another.</summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        var cosine = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (cosine > 0.999999f) return Quaternion.Identity;
        var axis = Vector3.Cross(from, to);
        if (cosine < -0.999999f || axis.LengthSquared() < 1e-12f)
            axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(cosine));
    }

    private void Prepare(ReadOnlySpan<int> parentIndices, ReadOnlySpan<Vector3> positions)
    {
        var count = parentIndices.Length;
        var same = parents.Length == count;
        for (var i = 0; same && i < count; i++)
            same = parents[i] == parentIndices[i];
        if (!same)
        {
            parents = parentIndices.ToArray();
            children = new List<int>[count];
            for (var i = 0; i < count; i++) children[i] = new List<int>();
            for (var i = 0; i < count; i++)
                if (parents[i] >= 0 && parents[i] < i) children[parents[i]].Add(i);
            structural = new bool[count];
            core = new bool[count];
            hanging = new bool[count];
            anchor = new int[count];
            radius = new float[count];
            clearance = new float[count];
            floor = new float[count];
            roof = new float[count];
            solved = new Vector3[count];
            turned = new Quaternion[count];
            limbEnd = new bool[count];
            legBones = new int[count];
            legSeen = new long[count];
            hipHigh = new float[count];
            hipSeen = new long[count];
            endLow = new float[count];
            endSeen = new long[count];
            heldTurn = new Quaternion[count];
            heldUp = new float[count];
            footTurn = new float[count];
        }
        Array.Clear(limbEnd);
        Array.Clear(heldUp);
        Array.Clear(footTurn);

        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (var p in positions)
        {
            lowest = MathF.Min(lowest, p.Y);
            highest = MathF.Max(highest, p.Y);
        }
        var height = MathF.Max(0.05f, highest - lowest);
        poseHeight = height;
        // (What a bone is taken for goes by a steadier height than the pose's: the tallest it has
        // been of late. A pose that ducks a little does not make a toe a bone that takes part.)
        if (!within)
        {
            steadyHeight = same && steadySeen == solves ? MathF.Max(height, steadyHeight - HipSink * height) : height;
            steadySeen = solves + 1;
        }
        var steady = steadyHeight;

        // How far each bone's descendants reach below it, along the bones.
        Span<float> reach = count <= 512 ? stackalloc float[count] : new float[count];
        reach.Clear();
        for (var i = count - 1; i > 0; i--)
        {
            var parent = parents[i];
            if (parent < 0 || parent >= i) continue;
            if (Parked(i, positions, height)) continue;
            reach[parent] = MathF.Max(reach[parent], reach[i] + Vector3.Distance(positions[i], positions[parent]));
        }

        // The skeleton's root sits at the owner's feet and carries the body on a bone that is not a
        // limb: it is the handle the animation moves the body by. It takes no part; what hangs from it does.
        for (var i = 0; i < count; i++)
        {
            var parent = parents[i];
            var length = parent >= 0 && parent < i ? Vector3.Distance(positions[i], positions[parent]) : 0f;
            structural[i] = parent >= 0 && parent < i && length + reach[i] >= StructuralShare * steady && !Parked(i, positions, height);
            // A bone only takes part if the chain down to it does.
            if (structural[i] && parents[parent] >= 0 && !structural[parent])
                structural[i] = false;
            core[i] = false;
        }

        // The body is held by one bone: of those hanging from the skeleton's root, the one that
        // carries most. Anything else hanging there (a marker bone) takes no part.
        pivotBone = -1;
        for (var i = 1; i < count; i++)
        {
            if (!structural[i] || structural[parents[i]]) continue;
            if (pivotBone < 0 || reach[i] > reach[pivotBone]) pivotBone = i;
        }
        for (var i = 1; i < count; i++)
            if (structural[i] && !structural[parents[i]] && i != pivotBone)
                Drop(i);
        if (pivotBone < 0)
            return;

        // The core: the bone the body is held by, the bones that branch, and all between them.
        core[pivotBone] = true;
        for (var i = count - 1; i > 0; i--)
        {
            if (!structural[i]) continue;
            var branches = 0;
            foreach (var child in children[i])
                if (structural[child] && reach[child] + Vector3.Distance(positions[child], positions[i]) >= BranchShare * steady)
                    branches++;
            if (branches >= 2 || core[i])
            {
                core[i] = true;
                if (structural[parents[i]]) core[parents[i]] = true;
            }
        }

        // A limb is one chain: from a bone on the core, outward along whichever bone carries most.
        // What branches off a limb along the way is carried by it, and takes no part of its own.
        limbs.Clear();
        var chain = new List<int>();
        for (var i = 1; i < count; i++)
        {
            if (!structural[i] || core[i] || !core[parents[i]]) continue;
            chain.Clear();
            for (var bone = i; bone >= 0;)
            {
                chain.Add(bone);
                var next = -1;
                var most = -1f;
                foreach (var child in children[bone])
                {
                    if (!structural[child]) continue;
                    var carries = reach[child] + Vector3.Distance(positions[child], positions[bone]);
                    if (carries > most) { most = carries; next = child; }
                }
                foreach (var child in children[bone])
                    if (child != next && structural[child]) Drop(child);
                bone = next;
            }
            limbs.Add(chain.ToArray());
        }

        // A pose that dips below its owner's feet (a hanging tail) stands on its own lowest point.
        var ground = 0f;
        for (var i = 0; i < count; i++)
            if (structural[i]) ground = MathF.Min(ground, positions[i].Y);
        poseGround = ground;

        for (var i = 0; i < count; i++)
        {
            anchor[i] = structural[i] ? i : parents[i] >= 0 && parents[i] < i ? anchor[parents[i]] : -1;
            if (!structural[i]) continue;
            // Thickness from the bone's own segment: the longest of the bones hanging from it, or its own.
            var segment = Vector3.Distance(positions[i], positions[parents[i]]);
            foreach (var child in children[i])
                if (structural[child])
                    segment = MathF.Max(segment, Vector3.Distance(positions[child], positions[i]));
            radius[i] = Math.Clamp(segment * RadiusPerLength, MinimumRadiusShare * height, MaximumRadiusShare * height);
            // A bone nearer the ground than it is thick already stands that near; it is not lifted for it.
            clearance[i] = ground + Math.Clamp(positions[i].Y - ground, 0f, radius[i]);
        }
    }

    /// <summary>
    /// Whether a bone is one the skeleton does not use: it sits at the skeleton's origin, far from
    /// the bone it hangs from (a model that has no use for a bone leaves it there). It is not a
    /// limb reaching for the ground.
    /// </summary>
    private bool Parked(int bone, ReadOnlySpan<Vector3> positions, float height)
    {
        var parent = parents[bone];
        return parent > 0 && Vector3.DistanceSquared(positions[bone], positions[0]) < 1e-4f * height * height
               && Vector3.Distance(positions[parent], positions[0]) > 0.05f * height;
    }

    /// <summary>Takes a bone and everything below it out of the fit; they are carried instead.</summary>
    private void Drop(int bone)
    {
        structural[bone] = false;
        foreach (var child in children[bone])
            if (structural[child]) Drop(child);
    }

    /// <summary>Turns the fitted positions into bone positions and rotations for every bone.</summary>
    private void Orient(Span<Vector3> positions, Span<Quaternion> rotations)
    {
        var count = positions.Length;
        Span<Vector3> from = stackalloc Vector3[16];
        Span<Vector3> to = stackalloc Vector3[16];
        for (var i = 0; i < count; i++)
        {
            turned[i] = Quaternion.Identity;
            if (!structural[i]) continue;

            var parent = parents[i];
            var inherited = structural[parent] ? turned[parent] : Quaternion.Identity;
            var pairs = 0;
            foreach (var child in children[i])
            {
                if (!structural[child] || pairs >= from.Length - 1) continue;
                var before = positions[child] - positions[i];
                var after = solved[child] - solved[i];
                if (before.LengthSquared() < 1e-10f || after.LengthSquared() < 1e-10f) continue;
                from[pairs] = before;
                to[pairs++] = after;
            }
            if (structural[parent] && pairs > 0)
            {
                var before = positions[parent] - positions[i];
                var after = solved[parent] - solved[i];
                if (before.LengthSquared() > 1e-10f && after.LengthSquared() > 1e-10f)
                {
                    from[pairs] = before;
                    to[pairs++] = after;
                }
            }
            turned[i] = pairs == 0 ? inherited : BestRotation(from[..pairs], to[..pairs], inherited);
            if (heldUp[i] > 0f)
                turned[i] = Quaternion.Slerp(turned[i], heldTurn[i], heldUp[i]);
            if (footTurn[i] != 0f)
                turned[i] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Cross(Vector3.UnitY, lastForward), footTurn[i]) * turned[i]);
            if (limbEnd[i])
                turned[i] = Quaternion.Normalize(KeepCarriedClear(i, positions, turned[i]));
        }

        // Write back, rigidly carrying the bones that took no part.
        Span<Vector3> original = count <= 512 ? stackalloc Vector3[count] : new Vector3[count];
        positions.CopyTo(original);
        for (var i = 0; i < count; i++)
        {
            var carrier = anchor[i];
            if (carrier < 0) continue;
            rotations[i] = Quaternion.Normalize(turned[carrier] * rotations[i]);
            positions[i] = carrier == i
                ? solved[i]
                : solved[carrier] + Vector3.Transform(original[i] - original[carrier], turned[carrier]);
        }
    }

    /// <summary>
    /// A limb's bone may carry small bones that take no part (fingers, toes). Turned as its limb
    /// turns it, they may point into the ground: a hand at the end of an arm that hangs down
    /// stands on its fingertips. It is then turned up about its own joint, by the least that brings
    /// all it carries clear: the hand lies on the ground. It turns the way what it carries already points
    /// along the ground, or forward when that is straight down.
    /// </summary>
    private Quaternion KeepCarriedClear(int bone, ReadOnlySpan<Vector3> positions, Quaternion turn)
    {
        var line = risen + 0.008f * poseHeight - solved[bone].Y;   // relative to the joint
        var lowest = Vector3.Zero;
        var any = false;
        for (var j = bone + 1; j < positions.Length; j++)
        {
            if (anchor[j] != bone) continue;
            var offset = Vector3.Transform(positions[j] - positions[bone], turn);
            if (!any || offset.Y < lowest.Y) lowest = offset;
            any = true;
        }
        if (!any || lowest.Y >= line) return turn;

        var reach = lowest.Length();
        var way = new Vector3(lowest.X, 0f, lowest.Z) + lastForward * (0.5f * reach);
        way = way.LengthSquared() > 1e-12f ? Vector3.Normalize(way) : lastForward;
        var hinge = Vector3.Cross(way, Vector3.UnitY);   // turns what points down towards the way it goes
        const int Steps = 36;
        float under = 0f, clear = MathF.PI;
        for (var step = 1; step <= Steps; step++)
        {
            var by = MathF.PI * step / Steps;
            if (CarriedClear(bone, positions, Quaternion.CreateFromAxisAngle(hinge, by) * turn, line)) { clear = by; break; }
            under = by;
        }
        for (var refine = 0; refine < 10; refine++)
        {
            var middle = (under + clear) * 0.5f;
            if (CarriedClear(bone, positions, Quaternion.CreateFromAxisAngle(hinge, middle) * turn, line)) clear = middle; else under = middle;
        }
        return Quaternion.CreateFromAxisAngle(hinge, clear) * turn;
    }

    private bool CarriedClear(int bone, ReadOnlySpan<Vector3> positions, Quaternion turn, float line)
    {
        for (var j = bone + 1; j < positions.Length; j++)
            if (anchor[j] == bone && Vector3.Transform(positions[j] - positions[bone], turn).Y < line - 1e-5f)
                return false;
        return true;
    }

    /// <summary>
    /// The rotation that best carries one set of directions onto another, starting from a guess: each
    /// pass turns by the mean of what every pair still asks for. With one pair it is the shortest turn
    /// from the guess, so a bone keeps the twist of the bone above it.
    /// </summary>
    private static Quaternion BestRotation(ReadOnlySpan<Vector3> from, ReadOnlySpan<Vector3> to, Quaternion guess)
    {
        var rotation = guess;
        for (var pass = 0; pass < 12; pass++)
        {
            var torque = Vector3.Zero;
            var weight = 0f;
            for (var i = 0; i < from.Length; i++)
            {
                var a = Vector3.Normalize(Vector3.Transform(from[i], rotation));
                var b = Vector3.Normalize(to[i]);
                var axis = Vector3.Cross(a, b);
                var sine = axis.Length();
                var cosine = Vector3.Dot(a, b);
                var w = to[i].Length();
                weight += w;
                if (sine < 1e-7f)
                {
                    // Opposite: any axis across will do to get started.
                    if (cosine < 0f)
                    {
                        var across = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
                        torque += Vector3.Normalize(across) * (MathF.PI * w);
                    }
                    continue;
                }
                torque += axis / sine * (MathF.Atan2(sine, cosine) * w);
            }
            if (weight <= 0f) break;
            torque /= weight;
            var angle = torque.Length();
            if (angle < 1e-5f) break;
            rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(torque / angle, angle) * rotation);
        }
        return rotation;
    }
}
