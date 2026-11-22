// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using static CombatSimulator.Camera.DeathShots.DeathShotMath;

namespace CombatSimulator.Camera.DeathShots;

/// <summary>One frame of input to <see cref="DeathShotSolver"/>.</summary>
public struct DeathShotInput
{
    public float Dt;
    /// <summary>The seven points of the body frame, world space.</summary>
    public BodyPoints Points;
    /// <summary>More points of the body (spine, neck, arms, legs) for whole-body framing; may be null. The same
    /// bones in the same order every frame.</summary>
    public IReadOnlyList<Vector3>? Extra;
    /// <summary>The positions now come from the ragdoll's rigid bodies.</summary>
    public bool PhysicsStarted;
    /// <summary>On the frame <see cref="PhysicsStarted"/> changes, when the caller can still read the points the old
    /// way (the animated bones) for this same frame: the solver then takes the difference between the two sources
    /// exactly. Without it, it assumes each point went on at the speed it had.</summary>
    public BodyPoints? PreviousSource;
    /// <summary><see cref="PreviousSource"/> for <see cref="Extra"/>.</summary>
    public IReadOnlyList<Vector3>? PreviousSourceExtra;
    /// <summary>A ragdoll is armed for this death: the death animation coming to rest is not "settled".</summary>
    public bool ExpectRagdoll;
    public DeathShotProfile Profile;
    /// <summary>Width / height of the view.</summary>
    public float Aspect;
    /// <summary>Body size relative to the reference body (1), when the caller knows it; 0 = estimate it from the
    /// spine length of the first frame.</summary>
    public float BodyScale;
    /// <summary>The player's left/right orbit, radians, summed since the shot began: the shot's azimuth is the
    /// camera's own when it began plus this, one to one. The solver never turns the camera about the body itself.</summary>
    public float UserYaw;
    /// <summary>The player's wheel on top of the solve: 1 = the shot's own size, larger = wider.</summary>
    public float UserSize;
    /// <summary>The option "Allow below ground": the shots whose recipe puts the eye below the subject looking up
    /// (the three of the dropdown) keep that placement on a body on the ground, with the eye under it, instead of
    /// giving way to ground level (<see cref="DeathShotSolver.GoesBelow"/>).</summary>
    public bool AllowBelowGround;
    /// <summary>The body is caught and held up (a grab): it is framed as a held body (<see cref="GrabbedFraming"/>)
    /// until it is let go and lies still again.</summary>
    public bool Grabbed;
    /// <summary>The option "When grabbed".</summary>
    public DeathShotGrabbedFraming GrabbedFraming;
}

/// <summary>What the solver hands back each frame: the pose for the game camera (eye and look-at through the pose
/// override, the vertical FoV through the coordinator) and what it decided, for the log and the offline check.</summary>
public struct DeathShotOutput
{
    public Vector3 Eye, LookAt;
    /// <summary>Vertical field of view, radians.</summary>
    public float Fov;
    /// <summary>The pose jumped this frame. Never for a change of shot (that is eased): only on the
    /// first frame of a shot that was not given a camera to start from, and when the eye was found inside terrain
    /// and put on its surface (<see cref="TerrainPops"/>).</summary>
    public bool Cut;
    public PoseClass Pose;
    /// <summary>The shot in use (never Auto).</summary>
    public DeathShotProfile Shot;
    /// <summary>The undamped solve of this frame.</summary>
    public Vector3 GoalEye, GoalLookAt;
    public float GoalFov;
    /// <summary>The damped pose before the built-in motion (the push-in) is added.</summary>
    public Vector3 BaseEye, BaseLookAt;
    public bool BodySettled;
    /// <summary>The body is settled and the shot holds its solve (the built-in motion may run).</summary>
    public bool Holding;
    /// <summary>The shot was changed this frame (the dropdown, the grab, the release): subject, elevation and
    /// distance ease over to the new one; the azimuth is not touched.</summary>
    public bool ShotChanged;
    public int ShotChanges;
    /// <summary>The azimuth of the eye about the look-at point that the shot is on: the camera's own when the shot
    /// began, plus the ease to a remembered direction when the shot was given one (<see cref="RecallOffset"/>), plus
    /// the player's orbit, and nothing else.</summary>
    public float Azimuth;
    /// <summary>How far the ease to the remembered direction has turned the shot so far (radians, signed; 0 when
    /// none was given), and whether it is still under way.</summary>
    public float RecallOffset;
    public bool Recalling;
    /// <summary>The body is framed as a held body (from the grab until it is let go and lies still).</summary>
    public bool Grabbed;
    /// <summary>Times the eye was found inside the terrain and had to be put on top of it (each is a cut frame).</summary>
    public int TerrainPops;
    /// <summary>The eye was pulled in (and the lens widened) this frame because something stood in the way.</summary>
    public bool PulledIn;
    /// <summary>The position source changed this frame (ragdoll start); <see cref="SourceJump"/> is how far the
    /// hips moved because of it, before the solver absorbed it.</summary>
    public bool SourceSwitched;
    public float SourceJump;
    /// <summary>Why the shot in use was taken: start, profile, grabbed, framing, released.</summary>
    public string Reason;
}

/// <summary>
/// The automatic death shot: every frame, from bone positions alone, a camera pose that frames the fallen body.
/// Game-free and deterministic; the plugin feeds it bones or ragdoll bodies and writes its output to the camera,
/// Tests/DeathShots feeds it recorded trajectories. Design and numbers: docs/DEATH_SHOTS.md.
///
/// The horizontal direction is the player's: the azimuth is the camera's own when the shot began plus the player's
/// orbit, one to one. The solver never chooses a side and never turns the camera about the body (where the face and
/// the body end up pointing cannot be known at the moment of death, so any choice would be set up and then
/// corrected). What it decides is the look-at point (the shot's subject), the elevation and the distance.
///
/// Per frame: body frame and pose class → the shot (the dropdown's, or the framing of a held body) → distance
/// (closed form: the smallest that keeps the shot's points inside the safe frame) → constraints (ground, water; a
/// blocked view pulls the eye in along its line) → damping → pose.
///
/// The camera is kept as a look-at point plus the eye's orbit about it (azimuth, elevation, distance). The look-at
/// point, the elevation and the distance each follow their goal through a critically damped spring with a speed
/// cap; the view's turn is held under 60°/s, not counting the player's own orbit.
/// </summary>
public sealed class DeathShotSolver
{
    // ---- numbers (docs/DEATH_SHOTS.md §4) ----
    public const float GroundClearance = 0.10f;
    public const float WaterClearance = 0.10f;
    public const float GroundRayLift = 1.5f;
    public const float LookSmoothTime = 0.30f;
    public const float EyeSmoothTime = 0.60f;
    public const float EntrySeconds = 2.5f;
    public const float EntryLookSmoothTime = 0.60f;
    public const float EntryEyeSmoothTime = 1.0f;
    public const float MaxLookSpeed = 3.0f;
    public const float MaxElevationRate = 35f * Deg;
    public const float MaxViewRate = 60f * Deg;
    public const float MaxEyeSpeed = 3.0f;
    public const float MaxDistanceRate = 2.0f;
    public const float MaxFovRate = 0.5f;
    public const float DeadZone = 0.03f;
    public const float SwitchSeconds = 0.5f;
    /// <summary>After a held body is let go, the shot of the lying body is taken up when it lies still, or after this.</summary>
    public const float ReleaseSeconds = 4f;
    /// <summary>A held body swings: the look-at point and the orbit follow on springs this much slower.</summary>
    public const float HeldLookDamping = 4f, HeldEyeDamping = 2f, HeldBlendSeconds = 0.5f;
    /// <summary>and the look-at point answers only when the subject has left a ball this wide (3 cm otherwise).</summary>
    public const float HeldDeadZone = 0.12f;
    /// <summary>The view of a subject lower than this above the ground under the body is tested from this height.</summary>
    public const float SunkLift = 0.03f;
    /// <summary>A blocked view: the eye comes in to this far in front of what blocks it, but no nearer the subject
    /// than <see cref="PullInLeast"/> (times the body's size) for a wall, and than <see cref="PullInLimit"/> of the
    /// shot's own distance for a rise in the ground (one nearer than that is left alone); the lens widens by up to <see cref="MaxFovWiden"/>
    /// to keep the subject's size; coming in is eased over <see cref="PullInSmoothTime"/> (going back out, like
    /// every other change of distance).</summary>
    public const float PullInMargin = 0.15f, PullInLeast = 0.45f, PullInLimit = 0.6f, PullInSmoothTime = 0.2f;
    /// <summary>While the player orbits (and for this long after), the distance follows its goal on this smooth time.</summary>
    public const float HandSmoothTime = 0.15f, HandSeconds = 0.5f;
    public const float MaxFovWiden = 1.35f;
    public const float PushInReach = 0.95f;
    /// <summary>The ease to a remembered direction (<see cref="Begin"/>): a smoothstep over this long, longer where
    /// the turn is so far that its fastest moment (1.5 × the mean) would pass <see cref="MaxRecallRate"/>; the
    /// player's hand ends it once it has turned the camera by <see cref="RecallCancel"/>.</summary>
    public const float RecallSeconds = 1.2f, MaxRecallRate = 150f * Deg, RecallCancel = 1f * Deg;
    public const float MotionDelay = 1.0f;
    public const float RampSeconds = 0.6f;
    public const float OutOfFrame = 1.02f;
    public const float Landing = 0.08f;
    /// <summary>Hips → chest → head of the body the recipes' lengths are written for (the user's own character in the
    /// recordings: its Low three-quarter then sits at 1.2 m, the middle of the old presets).</summary>
    public const float ReferenceSpine = 0.46f;
    public const float CalmSpeed = 0.4f;
    public const float CalmSeconds = 1.5f;
    /// <summary>Below-ground shots: a hit of the subject → eye ray counts as the ground the eye goes under, not as
    /// something in the way, when the ground query at the hit returns the hit's own height within this,</summary>
    public const float GroundHitTolerance = 0.08f;
    /// <summary>and that height is within this (times the body's size) of the ground under the body.</summary>
    public const float GroundHitBand = 0.5f;

    private readonly IDeathShotWorld world;
    private bool allowBelow;
    private bool grabbed, releasing, wasBelow, emerging;
    private float releaseAge, heldBlend, handBlend;

    /// <summary>
    /// With "Allow below ground" on, the shots that may put the eye under the ground: the low ones, whose eye sits
    /// below the subject looking up (all three of the dropdown). For them the eye is not kept above the ground or the
    /// water, is never "found inside terrain", and the ground it goes under does not count as blocking the view.
    /// The two shots of a held body are never affected.
    /// </summary>
    public static bool GoesBelow(ShotRecipe recipe) => recipe.Low;

    /// <summary>Only where the shot actually looks up from below its subject.</summary>
    private bool Below(ShotRecipe r, float elevation) => allowBelow && GoesBelow(r) && elevation < 0f;

    /// <summary>
    /// Whether something stands between the subject and the eye, and how far along. For a below-ground shot the
    /// ground the eye goes under is not that something. It is told from a wall or a bank in between by where and on
    /// what the ray is stopped: on a ray going down, at the level of the ground under the body (within
    /// <see cref="GroundHitBand"/> × body size), on a surface that faces up (its normal within 45° of straight up) —
    /// or, in a world that does not give normals, at a point that lies on the ground surface (the ground query
    /// straight down at that point returns the point's own height within <see cref="GroundHitTolerance"/>). A wall
    /// faces sideways (or has the ground well below the point); a bank or a ledge stops the ray away from the body's
    /// ground level; either still blocks.
    /// (What lies beyond the point where the ray enters the ground is under the ground and is not looked at.)
    /// </summary>
    private bool Obstructed(ShotRecipe r, Vector3 centre, Vector3 eye, float ground, out float hit, out bool groundLike)
    {
        // A subject that has sunk into the ground (a ragdoll pressed into a slope, a body the ground query puts
        // under the surface): the ray starts just above the ground, or the ground would block every side.
        if (centre.Y < ground + SunkLift) centre.Y = ground + SunkLift;
        groundLike = false;
        if (!world.Blocked(centre, eye, out hit, out var normal)) return false;
        // What stopped the ray faces up (within 45° of straight up): a rise in the ground, not a wall. (A world that
        // gives no normals: taken for a wall.)
        groundLike = normal.LengthSquared() > 0.25f && normal.Y / normal.Length() >= 0.7f;
        if (!Below(r, eye.Y - centre.Y)) return true;
        var along = eye - centre;
        var length = along.Length();
        if (length < 1e-4f || along.Y >= 0f) return true;
        var at = centre + along * (hit / length);
        if (MathF.Abs(at.Y - ground) > GroundHitBand * scale) return true;
        if (normal.LengthSquared() > 0.25f) return normal.Y / normal.Length() < 0.7f;
        return !(world.GroundBelow(at + new Vector3(0f, GroundRayLift, 0f)) is { } surface && MathF.Abs(surface - at.Y) <= GroundHitTolerance);
    }
    private readonly PoseClassifier classifier = new();
    private readonly SettleDetector settle = new();
    private readonly SettleDetector calm = new(CalmSpeed, CalmSeconds);
    private readonly List<(Vector3 P, float R)> fit = new();

    // start camera
    private bool hasStart;
    private Vector3 startEye, startLook;
    private float startFov;

    // body
    private bool has;
    private float time, scale = 1f, groundY;
    private BodyPoints lastPoints, beforeLastPoints, offset, smooth;
    private float lastDt;
    private bool hasTwoFrames;
    private Vector3[] beforeLastExtra = Array.Empty<Vector3>();
    private Vector3[] lastExtra = Array.Empty<Vector3>(), extraOffset = Array.Empty<Vector3>(), extraNow = Array.Empty<Vector3>();
    private bool lastPhysics;
    private float switchAge = float.MaxValue;

    // decision
    private ShotRecipe recipe = ShotRecipe.LowClose;
    /// <summary>The azimuth of the camera the shot began from: the player's orbit is added to it, nothing else.</summary>
    private float baseAzimuth;
    // the ease to a remembered direction
    private float? recallTarget;
    private float recallDelta, recallSeconds, recallAge, recallOffset;
    private bool recalling;
    private bool wasSettled;
    private int shotChanges, pops;
    private float rampAge;
    private string reason = "start";

    // hold
    private bool holding;
    private float holdFor;
    private BodyPoints heldPoints;
    private Vector3[] heldExtra = Array.Empty<Vector3>();
    private float heldGround;
    private float motion, motionVelocity, motionAmount = 1f;

    // damped state
    private Vector3 look, lookVelocity, heldLook;
    private float azimuth, elevation, distance, fov, heldDistance;
    private float azimuthVelocity, elevationVelocity, distanceVelocity, fovVelocity;
    private Vector3 lastEye, lastDirection;
    private bool hasOutput;

    public DeathShotSolver(IDeathShotWorld world) => this.world = world;

    public PoseClass Pose => classifier.Current;
    public float BodyScale => scale;

    /// <summary>Starts a death shot from the camera as it is now (the shot eases in from it). Without it the first
    /// frame is the solved shot itself. <paramref name="rememberedAzimuth"/>: a direction (world azimuth of the eye
    /// about the body) the player left the last death shot on: the shot turns to it from the camera's own, the
    /// shorter way round, on a smooth profile, while the height and the distance ease in. The player's hand ends that
    /// turn where it is and takes over. None: the shot stays on the camera's own azimuth.</summary>
    public void Begin(Vector3 eye, Vector3 lookAt, float verticalFov, float? rememberedAzimuth = null)
    {
        Reset();
        recallTarget = rememberedAzimuth is { } a && float.IsFinite(a) ? a : null;
        hasStart = true;
        startEye = eye;
        startLook = lookAt;
        startFov = verticalFov;
    }

    public void Reset()
    {
        hasStart = false;
        has = false;
        hasTwoFrames = false;
        hasOutput = false;
        time = 0f;
        scale = 1f;
        classifier.Reset();
        settle.Reset();
        calm.Reset();
        rampAge = 0f;
        switchAge = float.MaxValue;
        lastPhysics = false;
        wasSettled = false;
        shotChanges = pops = 0;
        grabbed = releasing = wasBelow = emerging = false;
        releaseAge = heldBlend = handBlend = 0f;
        recallTarget = null;
        recallDelta = recallSeconds = recallAge = recallOffset = 0f;
        recalling = false;
        reason = "start";
        holding = false;
        holdFor = 0f;
        motion = motionVelocity = 0f;
        lookVelocity = default;
        azimuthVelocity = elevationVelocity = distanceVelocity = fovVelocity = 0f;
        offset = default;
    }

    private struct Goal
    {
        public Vector3 Eye, LookAt, Centre;
        public float Fov, Distance, Azimuth, Elevation;
        /// <summary>How far the shot's points reach toward the frame edge (1 = the edge, more = outside).</summary>
        public float Reach;
    }

    public DeathShotOutput Step(in DeathShotInput input)
    {
        var dt = Math.Clamp(input.Dt, 0f, 0.1f);
        var aspect = input.Aspect > 0.1f ? input.Aspect : 16f / 9f;
        var userSize = input.UserSize > 0.01f ? Math.Clamp(input.UserSize, 0.5f, 3f) : 1f;
        allowBelow = input.AllowBelowGround;
        var output = new DeathShotOutput();

        // ---- 1. the body: source switch, ground, frame, class, settled ----
        var raw = input.Points;
        var extraCount = input.Extra?.Count ?? 0;
        if (extraNow.Length != extraCount)
        {
            extraNow = new Vector3[extraCount];
            lastExtra = new Vector3[extraCount];
            beforeLastExtra = new Vector3[extraCount];
            hasTwoFrames = false;
            extraOffset = new Vector3[extraCount];
            for (var i = 0; i < extraCount; i++) lastExtra[i] = input.Extra![i];
        }
        if (has && input.PhysicsStarted != lastPhysics)
        {
            // The positions now come from somewhere else (bone origins → rigid body centres): keep every point
            // where it was and let the difference fade, so the shot does not jump with the source.
            // "Where it was" is where it would be this frame at the speed it had (a body in mid-fall keeps falling).
            var carry = hasTwoFrames && lastDt > 1e-4f ? Math.Clamp(dt / lastDt, 0f, 2f) : 0f;
            var expected = input.PreviousSource is { IsFinite: true } old ? old : lastPoints.Offset(lastPoints.Minus(beforeLastPoints).Scaled(carry));
            offset = expected.Minus(raw);
            var oldExtra = input.PreviousSourceExtra is { } list && list.Count == extraCount ? list : null;
            for (var i = 0; i < extraCount; i++)
                extraOffset[i] = (oldExtra != null ? oldExtra[i] : lastExtra[i] + (lastExtra[i] - beforeLastExtra[i]) * carry) - input.Extra![i];
            switchAge = 0f;
            output.SourceSwitched = true;
            output.SourceJump = offset.Hips.Length();
            if (input.PhysicsStarted)
            {
                settle.Reset();
                calm.Reset();
                wasSettled = false;
            }
        }
        lastPhysics = input.PhysicsStarted;
        var fade = switchAge < SwitchSeconds ? 1f - SmoothStep(switchAge / SwitchSeconds) : 0f;
        var points = fade > 0f ? raw.Offset(offset.Scaled(fade)) : raw;
        for (var i = 0; i < extraCount; i++)
        {
            extraNow[i] = input.Extra![i] + (fade > 0f ? extraOffset[i] * fade : Vector3.Zero);
            beforeLastExtra[i] = lastExtra[i];
            lastExtra[i] = extraNow[i];
        }
        hasTwoFrames = has;
        beforeLastPoints = lastPoints;
        lastPoints = points;
        lastDt = dt;
        if (switchAge < float.MaxValue) switchAge += dt;

        var groundNow = world.GroundBelow(points.Hips + new Vector3(0f, GroundRayLift, 0f)) ?? (has ? groundY : points.FeetMid.Y);
        if (!has)
        {
            groundY = groundNow;
            smooth = points;
            // Body size from the spine (hips → chest → head): the one length among the seven points that no pose
            // changes much. The hips' height would do for a standing body only, and a death often starts crouched.
            scale = input.BodyScale > 0f ? input.BodyScale
                : (Vector3.Distance(points.Chest, points.Hips) + Vector3.Distance(points.Head, points.Chest)) / ReferenceSpine;
            scale = float.IsFinite(scale) ? Math.Clamp(scale, 0.4f, 4f) : 1f;
            classifier.Scale = scale;
        }
        else
        {
            groundY += (groundNow - groundY) * (1f - MathF.Exp(-dt / 0.2f));
            smooth = BodyPoints.Lerp(smooth, points, 1f - MathF.Exp(-dt / 0.1f));
        }
        var frame = BodyFrame.From(smooth, groundY);
        var pose = classifier.Update(frame, dt);

        var waitingForRagdoll = input.ExpectRagdoll && !input.PhysicsStarted;
        var settled = !waitingForRagdoll && settle.Update(points, dt);
        var calmNow = !waitingForRagdoll && calm.Update(points, dt);
        if (waitingForRagdoll)
        {
            settle.Reset();
            calm.Reset();
        }

        // ---- 2. hold: a settled body is framed from where it came to rest ----
        if (settled && !wasSettled)
        {
            holding = true;
            holdFor = 0f;
            heldPoints = points;
            heldExtra = (Vector3[])extraNow.Clone();
            heldGround = groundY;
        }
        else if (!settled && wasSettled)
        {
            holding = false;
        }
        var subjectPoints = holding ? heldPoints : points;
        var subjectExtra = holding ? heldExtra : extraNow;
        var subjectGround = holding ? heldGround : groundY;
        var subjectFrame = holding ? BodyFrame.From(heldPoints, heldGround) : frame;

        // ---- 3. the shot: the dropdown's, or the framing of a held body ----
        // Nothing here chooses a side: the horizontal direction is the player's (§4). A change of shot changes the
        // subject, the elevation and the distance only, and the damping eases it.
        var wasGrabbed = grabbed;
        if (input.Grabbed)
        {
            grabbed = true;
            releasing = false;
        }
        else if (grabbed)
        {
            // Let go: the held framing stays on the body while it falls, until it lies still (or will not).
            if (!releasing)
            {
                releasing = true;
                releaseAge = 0f;
            }
            releaseAge += dt;
            if (settled || calmNow || releaseAge > ReleaseSeconds) grabbed = releasing = false;
        }
        wasSettled = settled;
        var wanted = grabbed ? ShotRecipe.Of(input.GrabbedFraming) : ShotRecipe.Of(input.Profile);
        var changed = has && wanted != recipe;
        if (changed) reason = grabbed != wasGrabbed ? grabbed ? "grabbed" : "released" : grabbed ? "framing" : "profile";
        recipe = wanted;

        // ---- 4. this frame's goal: the player's azimuth, the shot's elevation and distance, the constraints ----
        if (!has)
        {
            // Where the camera is when the shot starts is where it stays: the azimuth of its eye about its look-at
            // point (straight above or below it there is none: the body's front then).
            var startAway = startEye - startLook;
            baseAzimuth = hasStart && startAway.X * startAway.X + startAway.Z * startAway.Z > 1e-6f
                ? Azimuth(startAway)
                : Azimuth(subjectFrame.FrontHorizontal);
            if (recallTarget is { } target)
            {
                // A remembered direction: the shorter way round to it, eased. (Without a camera to start from there
                // is nothing to ease from: the shot starts on it.)
                if (!hasStart) baseAzimuth = target;
                recallDelta = AngleDelta(baseAzimuth, target);
                recallSeconds = MathF.Max(RecallSeconds, 1.5f * MathF.Abs(recallDelta) / MaxRecallRate);
                recalling = MathF.Abs(recallDelta) > 1e-4f;
            }
        }
        else if (recalling)
        {
            // The player's hand on the camera ends the turn where it is: from here the azimuth is theirs alone.
            if (MathF.Abs(input.UserYaw) > RecallCancel) recalling = false;
            else
            {
                recallAge += dt;
                recallOffset = recallDelta * SmoothStep(recallAge / recallSeconds);
                if (recallAge >= recallSeconds)
                {
                    recallOffset = recallDelta;
                    recalling = false;
                }
            }
        }
        var wantedAzimuth = WrapAngle(baseAzimuth + recallOffset + input.UserYaw);
        var goal = Solve(recipe, wantedAzimuth, recipe.Elevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
        var pulled = false;
        if (Obstructed(recipe, goal.Centre, goal.Eye, subjectGround, out var hit, out var groundLike))
        {
            // Something stands between the subject and the eye: the eye comes in front of it, on the same line, and
            // the lens widens to keep the size (up to its cap). The side is not changed: that is the player's.
            // A wall: as far in as it takes, down to a least distance. A rise in the ground the view skims over (a
            // low shot of a body on the ground looks along it): only a little, and where the rise is nearer the body
            // than that the camera stays where it is rather than close in on a bump.
            var full = Vector3.Distance(goal.Centre, goal.Eye);
            var least = groundLike ? PullInLimit * full : MathF.Min(full, PullInLeast * scale);
            var keepDistance = MathF.Min(full, MathF.Max(hit - PullInMargin, least));
            if (full > 1e-4f && keepDistance < full && (!groundLike || hit >= least))
            {
                var ratio = keepDistance / full;
                goal.Eye = goal.Centre + (goal.Eye - goal.Centre) * ratio;
                goal.Fov = MathF.Min(2f * MathF.Atan(MathF.Tan(goal.Fov * 0.5f) / ratio), recipe.Fov * MaxFovWiden);
                FromEye(ref goal);
                pulled = true;
            }
        }

        // ---- 5. damping ----
        // The only jump left is the very first frame of a shot that was not given a camera to start from.
        var teleport = false;
        var pivot = look;
        var yawStep = 0f;
        if (!has)
        {
            if (hasStart)
            {
                look = heldLook = startLook;
                var away = startEye - startLook;
                distance = heldDistance = MathF.Max(0.1f, away.Length());
                elevation = MathF.Asin(Math.Clamp(away.Y / distance, -1f, 1f));
                fov = startFov;
            }
            else
            {
                Snap(goal);
                teleport = true;
            }
            azimuth = wantedAzimuth;
            heldLook = goal.LookAt;
            heldDistance = goal.Distance;
        }
        else
        {
            // Dead zone: the goal has to leave a 3 cm ball before the camera answers.
            var away = goal.LookAt - heldLook;
            var far = away.Length();
            // (A held body swings: the ball is wider while it is held, see heldEase below.)
            var dead = DeadZone + (HeldDeadZone - DeadZone) * SmoothStep(heldBlend);
            if (holding) heldLook = goal.LookAt; // a body at rest is framed exactly
            else if (far > dead) heldLook += away * ((far - dead) / far);
            var dd = goal.Distance - heldDistance;
            if (holding || pulled) heldDistance = goal.Distance;
            else if (MathF.Abs(dd) > dead) heldDistance += dd - MathF.Sign(dd) * dead;

            // The entry eases in on slower springs; a source switch doubles the damping for half a second.
            var entry = hasStart ? 1f - SmoothStep((time - (EntrySeconds - 0.5f)) / 1.0f) : 0f;
            // (The doubling comes in and goes out smoothly: a step in the spring constants would itself be felt.)
            var sinceSwitch = switchAge - dt; // 0 on the frame of the switch itself
            var bump = sinceSwitch < SwitchSeconds ? MathF.Sin(MathF.PI * MathF.Max(0f, sinceSwitch) / SwitchSeconds) : 0f;
            var boost = 1f + bump * bump;
            // A held body swings: the camera follows it on slower springs (they come in and go out over half a second).
            heldBlend = Math.Clamp(heldBlend + (grabbed ? dt : -dt) / HeldBlendSeconds, 0f, 1f);
            var heldEase = SmoothStep(heldBlend);
            var lookTime = (LookSmoothTime + (EntryLookSmoothTime - LookSmoothTime) * entry) * boost * (1f + (HeldLookDamping - 1f) * heldEase);
            var eyeTime = (EyeSmoothTime + (EntryEyeSmoothTime - EyeSmoothTime) * entry) * boost * (1f + (HeldEyeDamping - 1f) * heldEase);

            // The speed caps open over the first 0.6 s after the start and after a change of shot, so a far goal
            // is not answered with a jolt (the acceleration is bounded by cap / 0.6 s); the springs bring the camera
            // to rest without overshoot. Together: a smooth start and a smooth stop.
            if (changed) rampAge = 0f;
            rampAge += dt;
            var ramp = Math.Clamp(rampAge / RampSeconds, 0.02f, 1f);
            look = Spring(look, heldLook, ref lookVelocity, lookTime, MaxLookSpeed, dt);
            // The azimuth is the player's hand, one to one: no spring, no cap, nothing of the program's in it.
            yawStep = AngleDelta(azimuth, wantedAzimuth);
            azimuth = wantedAzimuth;
            // The elevation and the distance are the shot's: eased, the elevation capped so the eye stays under
            // 3 m/s on a long orbit. A blocked view is answered faster (the wall is in the picture meanwhile).
            var reach = MathF.Max(distance, 0.5f);
            var elevationRate = MathF.Min(MaxElevationRate, (MaxEyeSpeed - 1f) / reach) * ramp;
            elevation = Spring(elevation, goal.Elevation, ref elevationVelocity, eyeTime, elevationRate, dt);
            var comingIn = pulled && heldDistance < distance;
            // While the player turns the camera the distance keeps up with the turn (a whole body takes more room
            // seen across than along): it is their own move that changes the framing, and a slow answer would leave
            // the body cut off at the frame's edge until it arrives.
            handBlend = yawStep != 0f ? 1f : MathF.Max(0f, handBlend - dt / HandSeconds);
            var distanceTime = comingIn ? PullInSmoothTime : eyeTime + (HandSmoothTime - eyeTime) * MathF.Min(1f, handBlend * 2f);
            distance = Spring(distance, heldDistance, ref distanceVelocity, MathF.Min(distanceTime, eyeTime), MaxDistanceRate * (comingIn ? 1f : MathF.Max(ramp, handBlend)) * (1f + handBlend), dt);
            fov = Spring(fov, goal.Fov, ref fovVelocity, comingIn ? PullInSmoothTime : eyeTime, MaxFovRate, dt);
        }

        // ---- 6. built-in motion: only once the body rests and the shot has had time to arrive ----
        if (holding)
        {
            if (holdFor == 0f) PlanMotion(goal, aspect);
            holdFor += dt;
            var t = holdFor - MotionDelay;
            var want = t <= 0f ? 0f : SmoothStep(t / ShotRecipe.PushInSeconds);
            motion = want * motionAmount;
            motionVelocity = 0f;
        }
        else if (motion != 0f)
        {
            motion = Spring(motion, 0f, ref motionVelocity, EyeSmoothTime, 1f, dt);
            if (MathF.Abs(motion) < 1e-4f) motion = 0f;
        }
        var outDistance = distance * (1f - ShotRecipe.PushInAmount * motion);

        // ---- 7. the pose, kept off the ground and the water, and under the view-rate cap ----
        // A below-ground shot: the eye goes where the orbit puts it (still under the speed cap), ground or no ground.
        var below = Below(recipe, recipe.Elevation);
        // From a below-ground shot to one the ground holds, with the eye still under the ground: it comes up
        // through the ground at the speed cap (Advance), it is not put on top of it.
        if (wasBelow && !below) emerging = true;
        wasBelow = below;
        var baseEye = look + OrbitDirection(azimuth, elevation) * distance;
        if (!below) baseEye = Land(baseEye);
        var eye = look + OrbitDirection(azimuth, elevation) * outDistance;
        if (!below) eye = Land(eye);
        var lookAt = look;
        var popped = false;
        // The caps on the eye's speed and on the view's turn are for the program's own moves. The player's orbit is
        // not held back by them: last frame's eye and view are first carried round by this frame's turn of the hand.
        if (yawStep != 0f && hasOutput)
        {
            lastEye = pivot + TurnAboutUp(lastEye - pivot, yawStep);
            if (!below && !emerging) lastEye = Floor(lastEye);
            lastDirection = TurnAboutUp(lastDirection, yawStep);
        }
        if (below) eye = hasOutput && !teleport && dt > 0f ? Toward(lastEye, eye, dt) : eye;
        else eye = hasOutput && !teleport && dt > 0f ? Advance(lastEye, eye, dt, out popped) : Floor(eye);
        if (popped)
        {
            // The eye turned out to be inside the terrain and was put on top of it: a jump, and reported as one.
            teleport = true;
            pops++;
        }
        var toLook = lookAt - eye;
        var reachOut = toLook.Length();
        if (reachOut < 0.1f)
        {
            toLook = -OrbitDirection(azimuth, elevation) * 0.1f;
            reachOut = 0.1f;
            lookAt = eye + toLook;
        }
        var direction = toLook / reachOut;
        if (hasOutput && !teleport && has && dt > 0f)
        {
            var cos = Math.Clamp(Vector3.Dot(direction, lastDirection), -1f, 1f);
            var angle = MathF.Acos(cos);
            var allowed = MaxViewRate * dt;
            if (angle > allowed && angle > 1e-5f)
            {
                var axis = Vector3.Cross(lastDirection, direction);
                if (axis.LengthSquared() > 1e-12f)
                {
                    direction = Vector3.Transform(lastDirection, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), allowed));
                    lookAt = eye + direction * reachOut;
                }
            }
        }
        lastDirection = direction;
        lastEye = eye;
        hasOutput = true;
        has = true;
        time += dt;
        if (changed) shotChanges++;
        var cut = teleport;

        output.Eye = eye;
        output.LookAt = lookAt;
        output.Fov = fov;
        output.Cut = cut;
        output.Pose = pose;
        output.Shot = recipe.Profile;
        output.GoalEye = goal.Eye;
        output.GoalLookAt = goal.LookAt;
        output.GoalFov = goal.Fov;
        output.BaseEye = baseEye;
        output.BaseLookAt = look;
        output.BodySettled = settled;
        output.Holding = holding;
        output.ShotChanged = changed;
        output.ShotChanges = shotChanges;
        output.Azimuth = azimuth;
        output.RecallOffset = recallOffset;
        output.Recalling = recalling;
        output.Grabbed = grabbed;
        output.TerrainPops = pops;
        output.PulledIn = pulled;
        output.Reason = reason;
        return output;
    }

    /// <summary>A vector turned about the vertical so that its azimuth grows by <paramref name="angle"/>.</summary>
    private static Vector3 TurnAboutUp(Vector3 v, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return new Vector3(v.X * cos + v.Z * sin, v.Y, v.Z * cos - v.X * sin);
    }

    /// <summary>The push-in must not carry the subject out of the frame: one that would is halved, then dropped.
    /// (Out of the frame = past <see cref="PushInReach"/> of the way to its edge: from any azimuth the player may have
    /// chosen, along the body as well as across it, the near end of a whole body is the first to leave.)</summary>
    private void PlanMotion(in Goal goal, float aspect)
    {
        var tanV = MathF.Tan(goal.Fov * 0.5f);
        var tanH = tanV * aspect;
        foreach (var amount in stackalloc[] { 1f, 0.5f })
        {
            var end = goal.LookAt + (goal.Eye - goal.LookAt) * (1f - ShotRecipe.PushInAmount * amount);
            if (ReachOf(end, goal.LookAt, tanH, tanV) <= PushInReach) { motionAmount = amount; return; }
        }
        motionAmount = 0f;
    }

    private void Snap(in Goal goal)
    {
        look = goal.LookAt;
        azimuth = goal.Azimuth;
        elevation = goal.Elevation;
        distance = goal.Distance;
        fov = goal.Fov;
        lookVelocity = default;
        azimuthVelocity = elevationVelocity = distanceVelocity = fovVelocity = 0f;
    }

    /// <summary>
    /// A soft landing for an eye that comes down onto the ground while the body it follows keeps sinking (a shot
    /// from below the subject, when the subject falls): within a few centimetres of the floor the height bends
    /// smoothly into it instead of stopping dead, which would kink the view's pitch. Always above the floor; an
    /// eye resting on the ground sits <see cref="Landing"/> · ln 2 (5.5 cm) above the clearance.
    /// </summary>
    private Vector3 Land(Vector3 eye)
    {
        var floor = FloorAt(eye);
        if (!float.IsFinite(floor)) return eye;
        var x = (eye.Y - floor) / Landing;
        if (x < 20f) eye.Y = floor + Landing * (x < -20f ? 0f : MathF.Log(1f + MathF.Exp(x)));
        return eye;
    }

    /// <summary>The eye lifted to at least 10 cm above the ground and the water under it. Lifting can bring a higher
    /// surface into reach of the ray (a ledge above), so it is asked again until it holds.</summary>
    private Vector3 Floor(Vector3 eye)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var floor = FloorAt(eye);
            if (eye.Y >= floor) break;
            eye.Y = floor;
        }
        return eye;
    }

    /// <summary>
    /// Moves the eye toward where the damped orbit wants it, at no more than <see cref="MaxEyeSpeed"/>, without ever
    /// putting it under the ground. Gentle ground lifts it as it goes. Where the way leads into a bank or a ledge
    /// (the ground ahead stands higher than one frame may climb) the eye first rises where it is, at the speed
    /// cap, and goes on once it is high enough: no pop up the face of the bank, and no frame inside it.
    /// The eye may trail the orbit while it does; the view stays on the look-at point.
    /// An eye that a below-ground shot left under the ground (<see cref="emerging"/>) comes up through it the same
    /// way, at the speed cap, toward where it is wanted: it is not put on top.
    /// </summary>
    private Vector3 Advance(Vector3 from, Vector3 wanted, float dt, out bool popped)
    {
        popped = false;
        if (emerging)
        {
            if (from.Y < FloorAt(from) - 1e-3f) return Toward(from, wanted, dt);
            emerging = false;
        }
        var budget = MaxEyeSpeed * dt;
        var step = wanted - from;
        var length = step.Length();
        if (length > budget) step *= budget / length;
        var next = from + step;
        var lifted = Floor(next);
        // Free air, or ground within reach of this frame: go there (sliding along the ground if it lifts the eye).
        if (lifted.Y - MathF.Max(from.Y, next.Y) <= budget) return lifted;
        var up = new Vector3(from.X, from.Y + budget, from.Z);
        var onTop = Floor(up);
        if (onTop.Y - up.Y < 1e-4f) return up;
        // Rising brought a surface above the eye into reach of the ground ray: the eye stood inside something the
        // ray from 1.5 m above it could not see. There is nothing to slide along; it is put on top.
        popped = true;
        return onTop;
    }

    /// <summary>The speed cap of <see cref="Advance"/> alone, for an eye the ground does not hold (below-ground shots).</summary>
    private static Vector3 Toward(Vector3 from, Vector3 wanted, float dt)
    {
        var budget = MaxEyeSpeed * dt;
        var step = wanted - from;
        var length = step.Length();
        return length > budget ? from + step * (budget / length) : wanted;
    }

    private float FloorAt(Vector3 eye)
    {
        var from = eye + new Vector3(0f, GroundRayLift, 0f);
        var floor = float.NegativeInfinity;
        if (world.GroundBelow(from) is { } ground) floor = ground + GroundClearance;
        if (world.WaterBelow(from) is { } water) floor = MathF.Max(floor, water + WaterClearance);
        return floor;
    }

    private static void FromEye(ref Goal goal)
    {
        var away = goal.Eye - goal.LookAt;
        goal.Distance = MathF.Max(0.05f, away.Length());
        goal.Azimuth = Azimuth(away);
        goal.Elevation = MathF.Asin(Math.Clamp(away.Y / goal.Distance, -1f, 1f));
    }

    // ------------------------------------------------------------------ the solve

    /// <summary>
    /// The undamped shot from an azimuth: look-at point, eye, lens. Distance is closed form: with the view direction
    /// fixed, every point p asks for a retreat of at least (|x| + r) / (safe · tanHalf) − z along the view axis (x
    /// across, z along, from the subject's centre), and the largest request wins.
    /// </summary>
    private Goal Solve(ShotRecipe r, float az, float el, in BodyFrame frame, in BodyPoints p, Vector3[] extra, float ground, float aspect, float userSize)
    {
        var floorY = ground + 0.03f;
        fit.Clear();
        Vector3 centre;
        var headAxis = p.Head - p.Chest;
        headAxis = headAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(headAxis) : frame.Axis;
        switch (r.Subject)
        {
            case ShotSubject.HipsToChest:
                centre = (p.Hips + p.Chest) * 0.5f;
                fit.Add((p.Hips, 0.16f * scale));
                fit.Add((p.Chest, 0.16f * scale));
                break;
            case ShotSubject.Head:
                // j_kao sits at the base of the skull: the head's middle is a little further along the neck.
                centre = p.Head + headAxis * (0.09f * scale);
                fit.Add((centre, 0.14f * scale));
                break;
            case ShotSubject.HeadToThigh:
            {
                // A held body, from the top of the head down to the middle of the thighs. The aim point is half way
                // between the two (about the lower chest / belly on a hanging body), so the frame's margin above the
                // head and below the thighs is the same and the distance is what that extent takes: no empty band
                // above the head. The two ends are fitted as they are (2 cm), the trunk with its width.
                var top = p.Head + headAxis * (0.18f * scale);
                var thighs = (MidThigh(p.Hips, p.FootL) + MidThigh(p.Hips, p.FootR)) * 0.5f;
                centre = (top + thighs) * 0.5f;
                fit.Add((top, 0.02f * scale));
                fit.Add((thighs, 0.02f * scale));
                fit.Add((p.Head + headAxis * (0.09f * scale), 0.11f * scale));
                fit.Add((p.Chest, 0.16f * scale));
                fit.Add((p.Hips, 0.16f * scale));
                break;
            }
            default:
            {
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                void Take(Vector3 v, float radius)
                {
                    if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return;
                    // A ragdoll lets limbs sink into the floor; nobody can see those, and asking for them drags the shot back.
                    if (v.Y < floorY) v.Y = floorY;
                    fit.Add((v, radius));
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
                var radius = 0.10f * scale;
                Take(p.Hips, radius);
                Take(p.Chest, radius);
                Take(p.Head, radius);
                Take(p.Head + headAxis * (0.18f * scale), radius * 0.5f);
                Take(p.ShoulderL, radius);
                Take(p.ShoulderR, radius);
                Take(p.FootL, radius);
                Take(p.FootR, radius);
                foreach (var e in extra) Take(e, radius);
                centre = (min + max) * 0.5f;
                break;
            }
        }

        var fovNow = r.Fov;
        var tanV = MathF.Tan(fovNow * 0.5f);
        var tanH = tanV * aspect;
        var kx = r.SafeFrame * tanH;
        var ky = r.SafeFrame * tanV;
        var sized = r.FrameHeight > 0f ? r.FrameHeight * scale * 0.5f / tanV : 0f;
        var least = MathF.Max(sized, r.MinDistance * scale);
        var most = r.MaxDistance * scale * MathF.Max(1f, userSize);
        var goal = new Goal { Centre = centre, Fov = fovNow };

        // Free orbit about the subject's centre. Where the ground stops the eye, the elevation gives way
        // (the shot "degrades to ground level") and the distance is fitted again for the new direction.
        var elevationNow = el;
        var eye = centre;
        var d = least;
        for (var pass = 0; pass < 4; pass++)
        {
            var away = OrbitDirection(az, elevationNow);
            var fwd = -away;
            DynamicCameraSolver.BasisFromForward(fwd, out var right, out var up);
            d = least;
            foreach (var (point, radius) in fit)
            {
                var q = point - centre;
                var z = Vector3.Dot(q, fwd);
                d = MathF.Max(d, (MathF.Abs(Vector3.Dot(q, right)) + radius) / kx - z);
                d = MathF.Max(d, (MathF.Abs(Vector3.Dot(q, up)) + radius) / ky - z);
                d = MathF.Max(d, 0.3f - z);
            }
            d = MathF.Min(d * userSize, most);
            eye = centre + away * d;
            // A below-ground shot keeps the recipe's placement: nothing gives way to the ground.
            if (Below(r, el)) break;
            var floor = FloorAt(eye);
            if (eye.Y >= floor - 1e-4f) break;
            elevationNow = MathF.Asin(Math.Clamp((floor - centre.Y) / d, -1f, 1f));
        }
        goal.LookAt = centre;
        goal.Eye = Below(r, el) ? eye : Floor(eye);
        FromEye(ref goal);
        goal.Reach = ReachOf(goal.Eye, goal.LookAt, tanH, tanV);
        return goal;
    }

    /// <summary>The middle of a thigh: from the hips toward that leg's foot, half a thigh's length (the thigh is
    /// about half the leg; a leg folded shorter than that gives the point half way to the foot).</summary>
    private Vector3 MidThigh(Vector3 hips, Vector3 foot)
    {
        var leg = foot - hips;
        var length = leg.Length();
        return length < 1e-4f ? hips : hips + leg * (MathF.Min(MidThighLength * scale, length * 0.5f) / length);
    }

    /// <summary>Hips bone to the middle of the thigh on the reference body (about a third of the way to the sole).</summary>
    public const float MidThighLength = 0.30f;

    /// <summary>How far the points of the last <see cref="Solve"/> reach toward the frame edge from a pose (1 = the
    /// edge; more, or a point behind the eye = outside).</summary>
    private float ReachOf(Vector3 eye, Vector3 lookAt, float tanH, float tanV)
    {
        var fwd = lookAt - eye;
        if (fwd.LengthSquared() < 1e-8f) return float.MaxValue;
        fwd = Vector3.Normalize(fwd);
        DynamicCameraSolver.BasisFromForward(fwd, out var right, out var up);
        var reach = 0f;
        foreach (var (point, _) in fit)
        {
            var v = point - eye;
            var z = Vector3.Dot(v, fwd);
            if (z < 0.05f) return float.MaxValue;
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(Vector3.Dot(v, right)) / (z * tanH), MathF.Abs(Vector3.Dot(v, up)) / (z * tanV)));
        }
        return reach;
    }
}
