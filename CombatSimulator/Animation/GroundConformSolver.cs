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
    // Past the crouch's limit, a push of this share of the pose's height takes the body down on all
    // fours, and as much again brings it low over the ground.
    private const float StageShare = 0.20f;
    // How far back its thighs lean when it is as low as it goes, radians: kneeling they hang down.
    private const float LowestLean = MathF.PI / 3f;
    // Kneeling, as measured on a crawl made by hand: the foot is turned from pointing forward to
    // pointing back and 35 degrees down, toes trailing, and the head is lifted half way back to
    // where it looked while the neck stays in line with the back.
    private const float KneelingFootTurn = 145f * MathF.PI / 180f;
    private const float HeadKept = 0.5f;
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
    private float[] heldUp = Array.Empty<float>();              // and how much it is held up: 0 hangs, 1 held
    private readonly List<int[]> limbs = new();        // each: its bones outward; the first is fixed to the core
    private readonly List<Vector3[]?> folded = new();  // per limb: how it folds under the crouching body, or null if it only hangs
    private readonly List<float> closedUp = new();     // per limb: how far apart its ends still are when it is closed up; 0 if it only hangs
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
        var upright = CrouchLift(positions, forward, Lift(positions, pivot, axis, 0f, out _), groundHeight, out var past);

        // How each limb the body stands on is folded under the crouching body.
        folded.Clear();
        Span<Vector3> chain = stackalloc Vector3[64];
        for (var l = 0; l < limbs.Count; l++)
        {
            var limb = limbs[l];
            var bones = Leg(limb, positions);
            if (bones == 0) { folded.Add(null); continue; }
            for (var k = 0; k < bones; k++)
                chain[k] = positions[limb[k]] + new Vector3(0f, upright, 0f);
            var end = limb[bones - 1];
            FitChain(chain[..bones], limb, Planted(limb[0], end, chain[0], closedUp[l], positions), forward);
            LayClear(chain[..bones], limb, forward);
            folded.Add(chain[..bones].ToArray());
        }

        // It comes down further only by going down on its front, and how far is how far it is
        // pushed past what its legs allow. First on all fours: the core tips until it is level, and
        // the legs turn from folded under it to kneeling. Then low: the knees slide back.
        var angle = 0f;
        var kneeling = 0f;  // how far it is from crouching on its feet (0) to kneeling (1)
        var lean = 0f;      // how far back its thighs then lean, radians
        var stage = 0f;
        var deepest = float.NaN;
        var resting = Lift(positions, pivot, axis, 0f, out var standingTop);
        if (upright > resting + 1e-5f)
        {
            // How far the core has to tip to be level: by how steeply it stands over the bone it
            // is held by. A core that is level as it stands (four legs under a belly) does not
            // tip, and its legs do not turn.
            var middle = Vector3.Zero;
            for (var i = 0; i < count; i++)
                if (core[i]) middle += positions[i] - pivot;
            var level = Math.Clamp(MathF.Atan2(middle.Y, Vector3.Dot(middle, forward)), 0f, MathF.PI / 2f);
            if (middle.LengthSquared() < 1e-10f) level = 0f;
            var share = level / (MathF.PI / 2f);

            stage = Math.Clamp(past / (StageShare * poseHeight), 0f, 2f);
            deepest = groundHeight - past + 2f * StageShare * poseHeight;
            var down = Smooth(MathF.Min(stage, 1f));
            var lower = Smooth(MathF.Max(stage - 1f, 0f));
            angle = level * down;
            kneeling = share * down;
            lean = share * LowestLean * lower;

            // As low as it goes, it goes no lower: pushed deeper it is as it was there, and the
            // ground carries it.
            if (share > 0f && groundHeight > deepest + 1e-4f)
            {
                var carried = groundHeight - deepest;
                var fitted = Solve(parentIndices, positions, rotations, deepest, forward);
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
        var tipped = Quaternion.CreateFromAxisAngle(axis, angle);
        // The core rests on the ground, or on its legs: folded under it while it crouches, and on
        // its knees as it goes down on them.
        var held = upright > resting + 1e-5f ? upright : float.MinValue;
        if (kneeling > 0f)
        {
            var onKnees = KneelLift(positions, pivot, tipped, axis, kneeling, lean);
            // (Never lower than its knees let it: they are not pushed into the ground and out again.)
            if (onKnees > float.MinValue) held = MathF.Max(onKnees, upright + (onKnees - upright) * kneeling);
        }
        var raise = new Vector3(0f, MathF.Max(Lift(positions, pivot, axis, angle, out _), held), 0f);
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
                // What a limb that is held up ends in is lifted part of the way back: its first
                // bone stays in line with the core (a neck with the back) and what is beyond that
                // (a head) looks ahead, not at the ground.
                var upheld = 1f - hangs;
                var first = Quaternion.CreateFromAxisAngle(axis, angle * upheld);
                var beyond = Quaternion.CreateFromAxisAngle(axis, angle * upheld * (1f - HeadKept));
                chain[0] = solved[limb[0]];
                for (var k = 1; k < bones; k++)
                {
                    chain[k] = chain[k - 1] + Vector3.Transform(positions[limb[k]] - positions[limb[k - 1]], k == 1 ? first : beyond);
                    heldTurn[limb[k]] = beyond;
                    heldUp[limb[k]] = upheld;
                }
                // A limb that points down at the ground and can fold (an arm under a shoulder) is
                // stood on it, and folds as a leg does; and as a leg does, it reaches less far out
                // from under where it is held the lower that is. The steeper it points, the more
                // it is stood; one that trails (a tail) is laid instead.
                var last = limb[bones - 1];
                var under = floor[last] - chain[bones - 1].Y;
                var along = chain[bones - 1] - chain[0];
                if (bones >= 3 && under > 0f && along.LengthSquared() > 1e-10f)
                {
                    var steep = Smooth((-along.Y / along.Length() - 0.5f) / 0.3f);
                    if (steep > 0f)
                    {
                        var share = Math.Clamp((chain[0].Y - floor[last]) / -along.Y, 0f, 1f);
                        var stood = new Vector3(chain[0].X + along.X * share, floor[last], chain[0].Z + along.Z * share);
                        FitChain(chain[..bones], limb, Vector3.Lerp(chain[bones - 1], stood, steep), forward);
                    }
                }
                LayClear(chain[..bones], limb, forward);
            }
            hanging[limb[0]] = !stands;
            for (var k = 1; k < bones; k++)
            {
                solved[limb[k]] = chain[k];
                // (A foot is turned over on the way down to the knees, as feet are; it trails.)
                hanging[limb[k]] = !stands || k >= folded[l]!.Length;
                limbEnd[limb[k]] = true;
            }
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
    /// If the body stands on this limb, how many of its bones make the leg: down to the first that
    /// is low in the pose, which is where it stands (an ankle, a wrist). What is beyond that is the
    /// foot, and keeps to the ground as the animation has it. Zero for a limb that only hangs: one
    /// that cannot fold, that reaches nowhere near the ground, or that does not hang down.
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
            return positions[limb[0]].Y - positions[bone].Y > 0.5f * length ? k + 1 : 0;
        }
        return 0;
    }

    /// <summary>
    /// A limb the body stands on, for the given tip of the core, before any lift: every bone points
    /// part of the way from where the crouch has it to where it is when the body kneels. Kneeling,
    /// the thigh points the way it is animated (it hangs from the hip, and swings as it is moved),
    /// leant back by the given angle; the rest of the leg lies back along the ground behind the
    /// knee, and the foot trails from it, toes down.
    /// </summary>
    private void StandingLimb(int index, ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 axis, float kneeling, float lean, Span<Vector3> into)
    {
        var limb = limbs[index];
        var crouched = folded[index]!;
        var knee = Knee(limb, crouched.Length, positions);
        var leant = Quaternion.CreateFromAxisAngle(axis, lean);
        var laid = Quaternion.CreateFromAxisAngle(axis, MathF.PI / 2f);
        var trailing = Quaternion.CreateFromAxisAngle(axis, KneelingFootTurn);
        into[0] = pivot + Vector3.Transform(positions[limb[0]] - pivot, tipped);
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
            var kneels = Vector3.Transform(bone / length, k <= knee ? leant : k < crouched.Length ? laid : trailing);
            var hinge = Vector3.Cross(from, kneels);
            var turn = MathF.Atan2(hinge.Length(), Vector3.Dot(from, kneels));
            hinge = hinge.LengthSquared() > 1e-10f ? Vector3.Normalize(hinge) : axis;
            // A bone that has far to turn turns the way the body tips: from pointing forward to
            // pointing back by way of pointing down, never up over the body. (So a bone that has
            // half a turn to make does not go one way now and the other a moment later.)
            if (turn >= MathF.PI / 2f && Vector3.Dot(hinge, axis) < 0f)
            {
                hinge = -hinge;
                turn = 2f * MathF.PI - turn;
            }
            into[k] = into[k - 1] + Vector3.Transform(from, Quaternion.CreateFromAxisAngle(hinge, turn * kneeling)) * length;
        }
    }

    /// <summary>Which joint of a leg is its knee: its first, not counting a very short bone at the hip.</summary>
    private static int Knee(int[] limb, int bones, ReadOnlySpan<Vector3> positions)
    {
        var length = 0f;
        for (var k = 1; k < bones; k++)
            length += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
        var knee = 1;
        while (knee < bones - 1 && Vector3.Distance(positions[limb[knee]], positions[limb[0]]) < ShortBoneShare * length)
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
    /// or over it.
    /// </summary>
    private float KneelLift(ReadOnlySpan<Vector3> positions, Vector3 pivot, Quaternion tipped, Vector3 axis, float kneeling, float lean)
    {
        var lift = float.MinValue;
        Span<Vector3> chain = stackalloc Vector3[64];
        for (var l = 0; l < folded.Count; l++)
        {
            if (folded[l] == null) continue;
            var limb = limbs[l];
            var bones = folded[l]!.Length;
            StandingLimb(l, positions, pivot, tipped, axis, kneeling, lean, chain[..bones]);
            // (A knee made of two bones a few centimetres apart is on the ground with both.)
            var knee = Knee(limb, bones, positions);
            var length = 0f;
            for (var k = 1; k < bones; k++)
                length += Vector3.Distance(positions[limb[k]], positions[limb[k - 1]]);
            for (var k = 1; k < bones - 1; k++)
                if (k <= knee || Vector3.Distance(positions[limb[k]], positions[limb[knee]]) < ShortBoneShare * length)
                    lift = MathF.Max(lift, floor[limb[k]] - chain[k].Y);
        }
        return lift;
    }

    /// <summary>
    /// How low a creature can crouch: the least lift of the upright core at which every leg it
    /// stands on still fits between its hip and where it stands. A leg folds only so far; closed
    /// up, its two ends are still some distance apart, and the hip cannot come nearer the foot than
    /// that, nor so the ground it stands on. The push beyond it is what the legs hold the body up by.
    /// </summary>
    /// <param name="past">How far the ground has risen past where the legs were closed up; negative before, least when there are no legs.</param>
    private float CrouchLift(ReadOnlySpan<Vector3> positions, Vector3 forward, float least, float most, out float past)
    {
        var lift = least;
        past = float.MinValue;
        closedUp.Clear();
        Span<Vector3> chain = stackalloc Vector3[64];
        foreach (var limb in limbs)
        {
            var bones = Leg(limb, positions);
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
            past = MathF.Max(past, over);
        }
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
            heldTurn = new Quaternion[count];
            heldUp = new float[count];
        }
        Array.Clear(limbEnd);
        Array.Clear(heldUp);

        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (var p in positions)
        {
            lowest = MathF.Min(lowest, p.Y);
            highest = MathF.Max(highest, p.Y);
        }
        var height = MathF.Max(0.05f, highest - lowest);
        poseHeight = height;

        // How far each bone's descendants reach below it, along the bones.
        Span<float> reach = count <= 512 ? stackalloc float[count] : new float[count];
        reach.Clear();
        for (var i = count - 1; i > 0; i--)
        {
            var parent = parents[i];
            if (parent < 0 || parent >= i) continue;
            reach[parent] = MathF.Max(reach[parent], reach[i] + Vector3.Distance(positions[i], positions[parent]));
        }

        // The skeleton's root sits at the owner's feet and carries the body on a bone that is not a
        // limb: it is the handle the animation moves the body by. It takes no part; what hangs from it does.
        for (var i = 0; i < count; i++)
        {
            var parent = parents[i];
            var length = parent >= 0 && parent < i ? Vector3.Distance(positions[i], positions[parent]) : 0f;
            structural[i] = parent >= 0 && parent < i && length + reach[i] >= StructuralShare * height;
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
                if (structural[child] && reach[child] + Vector3.Distance(positions[child], positions[i]) >= BranchShare * height)
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
