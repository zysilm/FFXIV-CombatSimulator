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
    /// <summary>The player's left/right orbit on top of the solve, radians.</summary>
    public float UserYaw;
    /// <summary>The player's wheel on top of the solve: 1 = the shot's own size, larger = wider.</summary>
    public float UserSize;
    /// <summary>The option "Allow below ground": the shots whose recipe puts the eye below the subject looking up
    /// (Low three-quarter, and Face where it sits slightly low) keep that placement on a body on the ground, with
    /// the eye under it, instead of giving way to ground level (<see cref="DeathShotSolver.GoesBelow"/>).</summary>
    public bool AllowBelowGround;
    /// <summary>The body is caught and held up (a grab): it is framed as a held body (<see cref="GrabbedFraming"/>)
    /// until it is let go and lies still again.</summary>
    public bool Grabbed;
    /// <summary>The option "When grabbed".</summary>
    public DeathShotGrabbedFraming GrabbedFraming;
    /// <summary>The option "Side preference": which of a shot's sides is taken when it has more than one.</summary>
    public DeathShotSide SidePreference;
    /// <summary>Where the face points (unit vector, world; <see cref="DeathShotFace"/>), from the head bone's rotation.
    /// Zero = not known: the torso's front stands in. Only read when a side is chosen, never between.</summary>
    public Vector3 Face;
}

/// <summary>What the solver hands back each frame: the pose for the game camera (eye and look-at through the pose
/// override, the vertical FoV through the coordinator) and what it decided, for the log and the offline check.</summary>
public struct DeathShotOutput
{
    public Vector3 Eye, LookAt;
    /// <summary>Vertical field of view, radians.</summary>
    public float Fov;
    /// <summary>The pose jumped this frame. Never for a change of side or of shot (those are eased): only on the
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
    /// <summary>The side or the shot was changed this frame (not counting the first choice).</summary>
    public bool SideChanged;
    /// <summary>Changes of side so far; of those, how many because a settled body was moved and came to rest turned.</summary>
    public int SideChanges, SideChangesAfterMove;
    public int Fallbacks;
    /// <summary>On a frame with <see cref="SideChanged"/>: the angle between the old side and the new one, as seen
    /// from the subject (radians): how far the ease has to carry the eye round.</summary>
    public float SideTurn;
    /// <summary>The body is framed as a held body (from the grab until it is let go and lies still).</summary>
    public bool Grabbed;
    /// <summary>What the side preference did at the last choice of side, for the log ("nearest", "face side: met",
    /// "face side: the face points up, nearest", "body's left: not met, that side is blocked", ...).</summary>
    public string SideNote;
    /// <summary>Azimuth of the face's horizontal direction at the last choice of side (NaN: the face pointed up or
    /// down, or was not looked at).</summary>
    public float FaceAzimuth;
    /// <summary>Times the eye was found inside the terrain and had to be put on top of it (each is a cut frame).</summary>
    public int TerrainPops;
    /// <summary>The eye was pulled in (and the lens widened) this frame because something stood in the way.</summary>
    public bool PulledIn;
    /// <summary>The position source changed this frame (ragdoll start); <see cref="SourceJump"/> is how far the
    /// hips moved because of it, before the solver absorbed it.</summary>
    public bool SourceSwitched;
    public float SourceJump;
    /// <summary>Why the side was last chosen: start, settled, moved, calm, blocked, profile, grabbed, held, released, back.</summary>
    public string Reason;
}

/// <summary>
/// The automatic death shot: every frame, from bone positions alone, a camera pose that frames the fallen body.
/// Game-free and deterministic; the plugin feeds it bones or ragdoll bodies and writes its output to the camera,
/// Tests/DeathShots feeds it recorded trajectories. Design and numbers: docs/DEATH_SHOTS.md.
///
/// Per frame: body frame and pose class → side choice (only at the start, once when the body has settled, when
/// it settles again after being moved, when it is caught and held up and when it is let go; the side in use is kept
/// unless it is bad, and a change is always eased, never cut) → distance (closed form: the smallest that keeps the shot's points inside
/// the safe frame) → constraints (ground, water, blocked view) → damping → pose.
///
/// The camera is kept as a look-at point plus the eye's orbit about it (azimuth, elevation, distance). The look-at
/// point and the orbit each follow their goal through a critically damped spring with a speed cap, so the eye
/// travels around the body, never through it, and the view can be held under 60°/s by capping the two angles.
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
    public const float MaxAzimuthRate = 45f * Deg;
    public const float MaxElevationRate = 35f * Deg;
    public const float MaxViewRate = 60f * Deg;
    public const float MaxEyeSpeed = 3.0f;
    public const float MaxDistanceRate = 2.0f;
    public const float MaxFovRate = 0.5f;
    public const float DeadZone = 0.03f;
    public const float SwitchSeconds = 0.5f;
    /// <summary>The side in use is kept while it is within this of a side its shot's rule asks for (and the view is
    /// clear): beyond it the camera would be showing the body's back, or its wrong end.</summary>
    public const float KeepSide = 75f * Deg;
    /// <summary>After a held body is let go, the shot of the lying body is taken up when it lies still, or after this.</summary>
    public const float ReleaseSeconds = 4f;
    /// <summary>A held body is given another side only when the camera has been more than this off its front for
    /// <see cref="BackSeconds"/> (it sees the back only), or the view is blocked.</summary>
    public const float BackOnly = 110f * Deg;
    public const float BackSeconds = 2f;
    /// <summary>A held body swings: the look-at point and the orbit follow on springs this much slower.</summary>
    public const float HeldLookDamping = 4f, HeldEyeDamping = 2f, HeldBlendSeconds = 0.5f;
    /// <summary>and the look-at point answers only when the body's upper half has left a ball this wide (3 cm otherwise).</summary>
    public const float HeldDeadZone = 0.12f;
    /// <summary>The side of a held body is looked at this long after it was caught (it has been lifted by then).</summary>
    public const float HeldSideAfter = 1.2f;
    /// <summary>Looking for another side, a rule's second choice counts as this much further from the camera.</summary>
    public const float SecondChoicePenalty = 20f * Deg;
    /// <summary>A side this close to the one in use is not worth moving to (while the one in use is clear).</summary>
    public const float SmallTurn = 10f * Deg;
    /// <summary>Side preference: a candidate side counts as on the preferred side when it is within this (as a cosine)
    /// of the one most toward it; the side in use is on the wrong side only beyond the wider band.</summary>
    public const float PreferBand = 0.25f, PreferKeepBand = 0.45f;
    /// <summary>Side preference: how much of the face's direction must be horizontal for it to name a side — a face
    /// that looks up (17° off straight up is enough), and one that looks into the ground (7°: the way the head is
    /// turned).</summary>
    public const float FaceUpSideways = 0.30f, FaceDownSideways = 0.12f;
    /// <summary>After the side was changed because the view was blocked, it is not changed for that reason again
    /// before this long: where nothing is clear the camera stays put instead of trying side after side.</summary>
    public const float BlockedAgainAfter = 6f;
    /// <summary>The view of a subject lower than this above the ground under the body is tested from this height.</summary>
    public const float SunkLift = 0.03f;
    /// <summary>The last resort when every side is blocked: the same shot from this much higher, up to three times.</summary>
    public const float RaiseStep = 20f * Deg;
    public const float BlockedSeconds = 1.5f;
    public const float PullInLimit = 0.6f;
    public const float MaxFovWiden = 1.35f;
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
    private float releaseAge, backFor, heldBlend, heldFor, blockedWait;
    private bool heldSideDone;
    private DeathShotGrabbedFraming framing, lastFraming;

    /// <summary>
    /// With "Allow below ground" on, the shots that may put the eye under the ground: the low ones, whose eye sits
    /// below the subject looking up (all six of the dropdown). For them the eye is not kept above the ground or the
    /// water, is never "found inside terrain", and the ground it goes under does not count as blocking the view.
    /// The two shots of a held body are never affected.
    /// </summary>
    public static bool GoesBelow(ShotRecipe recipe) => recipe.Low;

    /// <summary>Only where the shot actually looks up from below its subject: a low shot whose side sits level with
    /// the subject or above it (the face of a body on its back) is held above the ground like before.</summary>
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
    private bool Obstructed(ShotRecipe r, Vector3 centre, Vector3 eye, float ground, out float hit)
    {
        // A subject that has sunk into the ground (a ragdoll pressed into a slope, a body the ground query puts
        // under the surface): the ray starts just above the ground, or the ground would block every side.
        if (centre.Y < ground + SunkLift) centre.Y = ground + SunkLift;
        if (!Below(r, eye.Y - centre.Y)) return world.Blocked(centre, eye, out hit);
        if (!world.Blocked(centre, eye, out hit, out var normal)) return false;
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
    private readonly List<Candidate> candidates = new();
    private readonly List<Candidate> preferred = new(), others = new();

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
    private ShotRecipe recipe = ShotRecipe.LowThreeQuarter;
    private float lockAzimuth, lockElevation;
    private bool wasSettled, firstSettleDone, lateDone, movedAfterRest;
    private float blockedFor;
    private int sideChanges, sideChangesAfterMove, fallbacks, pops;
    private float rampAge;
    private string reason = "start";
    private DeathShotProfile lastProfile;
    // side preference
    private DeathShotSide sidePreference, lastSidePreference;
    private Vector3 face;
    /// <summary>The direction the preference named at the last choice where it named one (the hold: see PreferredDirection).</summary>
    private Vector3? heldToward;
    private string sideNote = "nearest";
    private float faceAzimuth = float.NaN;

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
    /// <summary>The azimuth of the side in use (eye about the look-at point, world), before the player's orbit.</summary>
    public float SideAzimuth => lockAzimuth;
    public float BodyScale => scale;

    /// <summary>Starts a death shot from the camera as it is now (the shot eases in from it). Without it the first
    /// frame is the solved shot itself.</summary>
    public void Begin(Vector3 eye, Vector3 lookAt, float verticalFov)
    {
        Reset();
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
        sideChangesAfterMove = 0;
        switchAge = float.MaxValue;
        lastPhysics = false;
        wasSettled = firstSettleDone = lateDone = movedAfterRest = false;
        blockedFor = 0f;
        sideChanges = fallbacks = pops = 0;
        grabbed = releasing = wasBelow = emerging = false;
        releaseAge = backFor = heldBlend = heldFor = blockedWait = 0f;
        heldSideDone = false;
        reason = "start";
        heldToward = null;
        sideNote = "nearest";
        faceAzimuth = float.NaN;
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

    private struct Candidate
    {
        public ShotRecipe Recipe;
        public float Azimuth, Elevation;
        /// <summary>0 = what the side rule asks for; 1 = its second choice.</summary>
        public int Rank;
        /// <summary>Not on the side the side preference asks for.</summary>
        public bool Off;
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
            movedAfterRest = true;
        }
        var subjectPoints = holding ? heldPoints : points;
        var subjectExtra = holding ? heldExtra : extraNow;
        var subjectGround = holding ? heldGround : groundY;
        var subjectFrame = holding ? BodyFrame.From(heldPoints, heldGround) : frame;

        // ---- 3. side choice ----
        // A change of side is always eased (the damping below carries the camera over along the shorter arc):
        // nothing here cuts. The side in use is kept whenever it is not actually bad.
        var changed = false;
        var turn = 0f;
        framing = input.GrabbedFraming;
        sidePreference = input.SidePreference;
        face = input.Face;
        if (!has)
        {
            lastProfile = input.Profile;
            lastSidePreference = sidePreference;
            grabbed = input.Grabbed;
            var startAzimuth = hasStart ? Azimuth(startEye - startLook) : Azimuth(subjectFrame.FrontHorizontal);
            Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, startAzimuth, true, 0f, "start", out _);
            lastFraming = framing;
        }
        else if (input.Grabbed && (!grabbed || releasing))
        {
            // The body is caught and held up: the shot of the held body, eased into from where the camera is.
            releasing = false;
            if (!grabbed)
            {
                // The held shot from the side the camera is on: the body is still where it lay. Its side is looked
                // at once it has been lifted (below).
                grabbed = true;
                backFor = 0f;
                heldFor = 0f;
                heldSideDone = false;
                var held = ShotRecipe.Of(framing);
                turn = MathF.Acos(Math.Clamp(Vector3.Dot(OrbitDirection(lockAzimuth, lockElevation), OrbitDirection(lockAzimuth, held.Elevation)), -1f, 1f));
                recipe = held;
                lockElevation = held.Elevation;
                reason = "grabbed";
                changed = true;
                lastFraming = framing;
            }
        }
        else if (grabbed && !input.Grabbed && !releasing)
        {
            // Let go: the held shot stays on the body while it falls, until it lies still (below).
            releasing = true;
            releaseAge = 0f;
        }
        else if (grabbed && framing != lastFraming)
        {
            lastFraming = framing;
            changed = Choose(input.Profile, pose, frame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, KeepSide, "grabbed", out turn);
        }
        else if (grabbed && releasing)
        {
            releaseAge += dt;
            if (settled || calmNow || releaseAge > ReleaseSeconds)
            {
                // Released and at rest (or not coming to rest): back to the shot of the lying body.
                grabbed = releasing = false;
                lastProfile = input.Profile;
                changed = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, 0f, "released", out turn);
                firstSettleDone |= settled;
            }
        }
        else if (grabbed)
        {
            // Held: once the body has been lifted, the side is looked at (kept when it is on the body's front, else
            // the nearer three-quarter on its front). After that it is not looked at again as the body swings or
            // hangs still, only when it shows nothing but the body's back for a while (below) or the view is blocked.
            lastProfile = input.Profile;
            heldFor += dt;
            if (!heldSideDone && heldFor >= HeldSideAfter)
            {
                heldSideDone = true;
                changed = Choose(input.Profile, pose, frame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, KeepSide, "held", out turn);
            }
        }
        else if (sidePreference != lastSidePreference)
        {
            // The side preference was changed while dead: the side is looked at again (it stays if it already is
            // on the side now preferred).
            lastSidePreference = sidePreference;
            heldToward = null;
            changed = Choose(input.Profile, pose, grabbed ? frame : subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, KeepSide, "preference", out turn);
        }
        else if (input.Profile != lastProfile)
        {
            // The dropdown changed while dead: over to the new shot, by its side nearest the camera.
            lastProfile = input.Profile;
            changed = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, 0f, "profile", out turn);
        }
        else if (settled && !wasSettled)
        {
            // Once, when the body has come to rest: the side stays unless the body ended up showing it something
            // else (the shot's side rule is far off) or the view is blocked. A side already re-chosen on the way here
            // (calm, blocked) stays unless it cannot.
            var moved = firstSettleDone;
            changed = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false,
                lateDone && !moved ? MathF.PI : KeepSide, moved ? "moved" : "settled", out turn);
            firstSettleDone = true;
        }
        else if (calmNow && !settled && !firstSettleDone && !lateDone)
        {
            // A body that stays put but will not lie still (trampled, twitching): look once anyway.
            lateDone = true;
            changed = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, KeepSide, "calm", out turn);
        }
        wasSettled = settled;

        // A held body that has turned its back on the camera and keeps it there.
        if (grabbed && !releasing && has && heldSideDone)
        {
            var behind = Vector3.Dot(Horizontal(lockAzimuth), frame.FrontHorizontal) < MathF.Cos(BackOnly);
            backFor = behind ? backFor + dt : 0f;
            if (backFor > BackSeconds)
            {
                backFor = 0f;
                changed |= Choose(input.Profile, pose, frame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, 0f, "back", out var backTurn);
                turn = MathF.Max(turn, backTurn);
            }
        }

        // ---- 4. distance and constraints: this frame's goal ----
        if (blockedWait > 0f) blockedWait -= dt;
        var goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
        var pulled = false;
        if (Obstructed(recipe, goal.Centre, goal.Eye, subjectGround, out var hit))
        {
            var full = Vector3.Distance(goal.Centre, goal.Eye);
            if (hit >= PullInLimit * full)
            {
                // Something in the way, but not close: come in front of it and widen the lens to keep the size.
                var keepDistance = MathF.Max(hit - 0.15f, PullInLimit * full);
                var ratio = keepDistance / full;
                goal.Eye = goal.Centre + (goal.Eye - goal.Centre) * ratio;
                goal.Fov = MathF.Min(2f * MathF.Atan(MathF.Tan(goal.Fov * 0.5f) / ratio), recipe.Fov * MaxFovWiden);
                FromEye(ref goal);
                pulled = true;
                blockedFor = 0f;
            }
            else
            {
                blockedFor += dt;
                if (blockedFor > BlockedSeconds && blockedWait <= 0f && has)
                {
                    blockedFor = 0f;
                    blockedWait = BlockedAgainAfter;
                    lateDone = true;
                    changed |= Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, 0f, "blocked", out var blockedTurn);
                    turn = MathF.Max(turn, blockedTurn);
                    goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
                }
            }
        }
        else if (goal.Reach > OutOfFrame)
        {
            // Nothing in the way, but the ground leaves the subject outside the frame from here: the same remedy.
            blockedFor += dt;
            if (blockedFor > BlockedSeconds && blockedWait <= 0f && has)
            {
                blockedFor = 0f;
                blockedWait = BlockedAgainAfter;
                lateDone = true;
                changed |= Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, false, 0f, "blocked", out var blockedTurn);
                turn = MathF.Max(turn, blockedTurn);
                goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
            }
        }
        else
        {
            blockedFor = 0f;
        }

        // ---- 5. damping ----
        // The only jump left is the very first frame of a shot that was not given a camera to start from.
        var teleport = false;
        if (!has)
        {
            if (hasStart)
            {
                look = heldLook = startLook;
                var away = startEye - startLook;
                distance = heldDistance = MathF.Max(0.1f, away.Length());
                azimuth = Azimuth(away);
                elevation = MathF.Asin(Math.Clamp(away.Y / distance, -1f, 1f));
                fov = startFov;
            }
            else
            {
                Snap(goal);
                teleport = true;
            }
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
            if (holding) heldDistance = goal.Distance;
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

            // The speed caps open over the first 0.6 s after the start and after a change of side, so a far goal
            // is not answered with a jolt (the acceleration is bounded by cap / 0.6 s); the springs bring the camera
            // to rest on the new side without overshoot. Together: a smooth start and a smooth stop.
            if (changed) rampAge = 0f;
            rampAge += dt;
            var ramp = Math.Clamp(rampAge / RampSeconds, 0.02f, 1f);
            look = Spring(look, heldLook, ref lookVelocity, lookTime, MaxLookSpeed, dt);
            // Angles: capped so the view turns under 60°/s and the eye stays under 3 m/s on a long orbit. The azimuth
            // goes the shorter way round.
            var reach = MathF.Max(distance, 0.5f);
            var azimuthRate = MathF.Min(MaxAzimuthRate, (MaxEyeSpeed - 1f) / reach) * ramp;
            var elevationRate = MathF.Min(MaxElevationRate, (MaxEyeSpeed - 1f) / reach) * ramp;
            var azimuthGoal = azimuth + AngleDelta(azimuth, goal.Azimuth);
            azimuth = Spring(azimuth, azimuthGoal, ref azimuthVelocity, eyeTime, azimuthRate, dt);
            elevation = Spring(elevation, goal.Elevation, ref elevationVelocity, eyeTime, elevationRate, dt);
            distance = Spring(distance, heldDistance, ref distanceVelocity, eyeTime, MaxDistanceRate * ramp, dt);
            fov = Spring(fov, goal.Fov, ref fovVelocity, eyeTime, MaxFovRate, dt);
            azimuth = WrapAngle(azimuth);
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
        var outAzimuth = azimuth;
        var outDistance = distance;
        outDistance *= 1f - ShotRecipe.PushInAmount * motion;

        // ---- 7. the pose, kept off the ground and the water, and under the view-rate cap ----
        // A below-ground shot: the eye goes where the orbit puts it (still under the speed cap), ground or no ground.
        var below = Below(recipe, lockElevation);
        // From a below-ground shot to one the ground holds, with the eye still under the ground: it comes up
        // through the ground at the speed cap (Advance), it is not put on top of it.
        if (wasBelow && !below) emerging = true;
        wasBelow = below;
        var baseEye = look + OrbitDirection(azimuth, elevation) * distance;
        if (!below) baseEye = Land(baseEye);
        var eye = look + OrbitDirection(outAzimuth, elevation) * outDistance;
        if (!below) eye = Land(eye);
        var lookAt = look;
        var popped = false;
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
            toLook = -OrbitDirection(outAzimuth, elevation);
            reachOut = 0.1f;
            lookAt = eye + toLook * reachOut;
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
        if (changed)
        {
            sideChanges++;
            if (movedAfterRest) sideChangesAfterMove++;
        }
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
        output.SideChanged = changed;
        output.SideChanges = sideChanges;
        output.SideChangesAfterMove = sideChangesAfterMove;
        output.SideTurn = turn;
        output.Grabbed = grabbed;
        output.SideNote = sideNote;
        output.FaceAzimuth = faceAzimuth;
        output.Fallbacks = fallbacks;
        output.TerrainPops = pops;
        output.PulledIn = pulled;
        output.Reason = reason;
        return output;
    }

    /// <summary>The push-in must not carry the subject out of the frame: one that would is halved, then dropped.</summary>
    private void PlanMotion(in Goal goal, float aspect)
    {
        var tanV = MathF.Tan(goal.Fov * 0.5f);
        var tanH = tanV * aspect;
        foreach (var amount in stackalloc[] { 1f, 0.5f })
        {
            var end = goal.LookAt + (goal.Eye - goal.LookAt) * (1f - ShotRecipe.PushInAmount * amount);
            if (ReachOf(end, goal.LookAt, tanH, tanV) <= 1f) { motionAmount = amount; return; }
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

    // ------------------------------------------------------------------ side choice

    /// <summary>
    /// Picks the shot and the side; returns whether they changed (the first choice of a shot is not a change) and by
    /// what angle the eye's direction about the subject turns.
    ///
    /// The side in use is kept when it is not bad: its shot is still one the pose allows, it is within
    /// <paramref name="keepWithin"/> of a side the shot's rule asks for (so it does not show the body's back to a
    /// shot of its front, or the wrong end), and the view from it is not blocked. Not moving beats a better side.
    ///
    /// Otherwise the candidates of the allowed shots are tried, the rule's first choices before its second ones and,
    /// among those, the nearest to <paramref name="fromAzimuth"/> (where the camera is) first, so the way over is
    /// short; one whose view of the subject is blocked is passed over, then the azimuth is stepped ±15° up to ±90°,
    /// then the spare shots are tried the same way. The change itself is eased by the damping, never cut.
    /// </summary>
    private bool Choose(DeathShotProfile profile, PoseClass pose, in BodyFrame frame, in BodyPoints points,
        Vector3[] extra, float ground, float aspect, float userSize, float fromAzimuth, bool first, float keepWithin, string why, out float turn)
    {
        turn = 0f;
        candidates.Clear();
        if (grabbed)
            AddCandidates(ShotRecipe.Of(framing), pose, frame);
        else if (profile == DeathShotProfile.Auto)
            foreach (var r in ShotRecipe.ForAuto(pose)) AddCandidates(r, pose, frame);
        else
            AddCandidates(ShotRecipe.Of(profile), pose, frame);
        // A shot with no side at all for this pose (the face of a body lying on it): the family's first shot.
        if (candidates.Count == 0) AddCandidates(ShotRecipe.LowThreeQuarter, pose, frame);
        // The first choice of a shot: the rule's first choices before its second ones, the nearest of those to the
        // camera first. A later one: the nearest to the camera, a second choice counting as 20° further away.
        if (first)
            candidates.Sort((a, b) => a.Rank != b.Rank
                ? a.Rank.CompareTo(b.Rank)
                : MathF.Abs(AngleDelta(fromAzimuth, a.Azimuth)).CompareTo(MathF.Abs(AngleDelta(fromAzimuth, b.Azimuth))));
        else
            candidates.Sort((a, b) => (MathF.Abs(AngleDelta(fromAzimuth, a.Azimuth)) + a.Rank * SecondChoicePenalty)
                .CompareTo(MathF.Abs(AngleDelta(fromAzimuth, b.Azimuth)) + b.Rank * SecondChoicePenalty));

        // The side preference: the candidates on the preferred side come first, in the order they had. A candidate is
        // on that side when it is as much toward it as the best one, within a band (two sides the same way round count
        // alike, and the order above decides between them). The side in use is on the wrong side only well outside the
        // band, so a direction that wavers does not send the camera across.
        var prefer = PreferredDirection(frame, out var note);
        var wrongSide = false;
        var towardBest = float.MinValue;
        if (prefer is { } toward)
        {
            foreach (var c in candidates) towardBest = MathF.Max(towardBest, Vector3.Dot(Horizontal(c.Azimuth), toward));
            preferred.Clear();
            others.Clear();
            foreach (var c in candidates)
            {
                var one = c;
                one.Off = Vector3.Dot(Horizontal(c.Azimuth), toward) < towardBest - PreferBand;
                (one.Off ? others : preferred).Add(one);
            }
            candidates.Clear();
            candidates.AddRange(preferred);
            candidates.AddRange(others);
            wrongSide = !first && Vector3.Dot(Horizontal(lockAzimuth), toward) < towardBest - PreferKeepBand;
        }
        sideNote = prefer != null && !wrongSide && !first ? note + ": met" : note;
        var couldKeep = false;
        var keepElevation = lockElevation;

        if (!first && keepWithin > 0f)
        {
            // A side that was already looked for on the way here (the body would not lie still, or the view was
            // blocked) stays when the body comes to rest, whatever shot it ended on, unless it cannot: the view is
            // blocked, or it is a shot of the face of a body that lies on it (and not the shot that was asked for).
            if (keepWithin >= MathF.PI)
            {
                var faceDown = pose == PoseClass.Prone && recipe.Profile is DeathShotProfile.LowFace or DeathShotProfile.HipsToFace;
                var asked = false;
                foreach (var c in candidates) asked |= c.Recipe == recipe;
                if ((asked || !faceDown) && !IsBlocked(recipe, lockAzimuth, lockElevation, frame, points, extra, ground, aspect, userSize))
                {
                    // (The preference was applied when that side was looked for; it is not applied a second time.)
                    if (wrongSide) sideNote = note + ": kept the side chosen earlier";
                    return false;
                }
            }
            if (!couldKeep)
            {
                keepWithin = MathF.Min(keepWithin, recipe.KeepWithin);
                // Keep the side: the shot is still allowed, the side is near enough to one its rule asks for, the view is clear.
                var nearest = float.MaxValue;
                var elevationThere = lockElevation;
                foreach (var c in candidates)
                {
                    if (c.Recipe != recipe) continue;
                    var off = MathF.Abs(AngleDelta(lockAzimuth, c.Azimuth));
                    if (off < nearest) { nearest = off; elevationThere = c.Elevation; }
                }
                if (nearest <= keepWithin && !IsBlocked(recipe, lockAzimuth, lockElevation, frame, points, extra, ground, aspect, userSize))
                {
                    // (The elevation follows the body where the shot takes it from the body.)
                    if (!wrongSide)
                    {
                        lockElevation = elevationThere;
                        return false;
                    }
                    couldKeep = true;
                    keepElevation = elevationThere;
                }
            }
        }

        Candidate? pick = null;
        if (couldKeep)
        {
            // The side in use is fine but for the preference: it is only left for a clear side on the preferred side.
            foreach (var c in candidates)
            {
                if (c.Off || IsBlocked(c.Recipe, c.Azimuth, c.Elevation, frame, points, extra, ground, aspect, userSize)) continue;
                pick = c;
                break;
            }
            if (pick == null)
            {
                lockElevation = keepElevation;
                sideNote = note + ": not met, that side is blocked";
                return false;
            }
        }
        else
        {
            foreach (var c in candidates)
            {
                if (IsBlocked(c.Recipe, c.Azimuth, c.Elevation, frame, points, extra, ground, aspect, userSize)) continue;
                pick = c;
                break;
            }
        }
        if (pick == null && candidates.Count > 0)
        {
            var best = candidates[0];
            for (var k = 1; k <= 6 && pick == null; k++)
            for (var sign = -1; sign <= 1 && pick == null; sign += 2)
            {
                var az = best.Azimuth + sign * k * 15f * Deg;
                if (!IsBlocked(best.Recipe, az, best.Elevation, frame, points, extra, ground, aspect, userSize))
                    pick = new Candidate { Recipe = best.Recipe, Azimuth = az, Elevation = best.Elevation };
            }
            // Nothing level with the body is clear (it lies in a pit, against a bank on every side, over an edge): the
            // same shot from higher up, a step at a time, on the nearest side first. (Before another shot: the one
            // that was asked for, from where it can be had.)
            for (var lift = 1; lift <= 3 && pick == null; lift++)
            for (var i = 0; i < candidates.Count && pick == null; i++)
            {
                var higher = MathF.Max(candidates[i].Elevation, 0f) + lift * RaiseStep;
                if (!IsBlocked(candidates[i].Recipe, candidates[i].Azimuth, higher, frame, points, extra, ground, aspect, userSize))
                    pick = new Candidate { Recipe = candidates[i].Recipe, Azimuth = candidates[i].Azimuth, Elevation = higher };
            }
            if (pick == null && !grabbed)
            {
                // Still nothing: the spare shots of the pose, nearest side first.
                var count = candidates.Count;
                foreach (var r in ShotRecipe.AutoSpare(pose))
                    if (r != best.Recipe) AddCandidates(r, pose, frame);
                candidates.Sort(count, candidates.Count - count, Comparer<Candidate>.Create((a, b) =>
                    MathF.Abs(AngleDelta(fromAzimuth, a.Azimuth)).CompareTo(MathF.Abs(AngleDelta(fromAzimuth, b.Azimuth)))));
                for (var i = count; i < candidates.Count && pick == null; i++)
                    if (!IsBlocked(candidates[i].Recipe, candidates[i].Azimuth, candidates[i].Elevation, frame, points, extra, ground, aspect, userSize))
                        pick = candidates[i];
            }
            if (pick != null) fallbacks++;
            pick ??= best;
        }
        if (pick == null) return false;

        var chosen = pick.Value;
        reason = why;
        if (prefer is { } wanted)
            sideNote = Vector3.Dot(Horizontal(chosen.Azimuth), wanted) >= towardBest - PreferBand ? note + ": met" : note + ": not met, that side is blocked";
        if (first)
        {
            recipe = chosen.Recipe;
            lockAzimuth = chosen.Azimuth;
            lockElevation = chosen.Elevation;
            return false;
        }
        turn = MathF.Acos(Math.Clamp(Vector3.Dot(OrbitDirection(lockAzimuth, lockElevation), OrbitDirection(chosen.Azimuth, chosen.Elevation)), -1f, 1f));
        // The same side again, or one a few degrees from it while this one is clear: nothing moves.
        var same = chosen.Recipe == recipe && (turn < 1f * Deg ||
            turn < SmallTurn && !IsBlocked(recipe, lockAzimuth, lockElevation, frame, points, extra, ground, aspect, userSize));
        if (same && turn >= 1f * Deg)
        {
            turn = 0f;
            return false;
        }
        recipe = chosen.Recipe;
        lockAzimuth = chosen.Azimuth;
        lockElevation = chosen.Elevation;
        if (same) turn = 0f;
        return !same;
    }

    /// <summary>
    /// The horizontal direction the side preference asks the eye to be toward, or null when it asks for nothing here
    /// (Nearest; a face that points up or down with nothing sideways to go by; a body lying on its side for "body's
    /// left / right"). The face is the head's (<see cref="DeathShotInput.Face"/>), the torso's front only when the head
    /// is not known. A face that points at the sky gives no side; one that points into the ground gives the way the
    /// head is turned, however little (the cheek that is up). The hold: once the preference has named a side, a face
    /// that points up or down at a later choice keeps naming that side (it does not fall back to "nearest", and a
    /// few degrees of tilt the other way do not send the camera across).
    /// </summary>
    private Vector3? PreferredDirection(in BodyFrame frame, out string note)
    {
        faceAzimuth = float.NaN;
        switch (sidePreference)
        {
            case DeathShotSide.FaceSide:
            case DeathShotSide.BehindHead:
            {
                var name = sidePreference == DeathShotSide.FaceSide ? "face side" : "behind the head";
                var f = face.LengthSquared() > 0.25f ? Vector3.Normalize(face) : frame.Front;
                var flat = new Vector3(f.X, 0f, f.Z);
                var length = flat.Length();
                if (length < (f.Y > 0f ? FaceUpSideways : FaceDownSideways))
                {
                    if (heldToward is { } before)
                    {
                        note = name + (f.Y > 0f ? ": the face points up, the side it named before" : ": the face points down, the side it named before");
                        return before;
                    }
                    note = name + (f.Y > 0f ? ": the face points up, nearest" : ": the face points down, nearest");
                    return null;
                }
                flat /= length;
                faceAzimuth = Azimuth(flat);
                note = name;
                heldToward = sidePreference == DeathShotSide.FaceSide ? flat : -flat;
                return heldToward;
            }
            case DeathShotSide.BodyLeft:
            case DeathShotSide.BodyRight:
            {
                var name = sidePreference == DeathShotSide.BodyLeft ? "body's left" : "body's right";
                // Lateral runs from the left shoulder to the right one.
                var flat = new Vector3(frame.Lateral.X, 0f, frame.Lateral.Z);
                var length = flat.Length();
                if (length < FaceUpSideways)
                {
                    if (heldToward is { } before)
                    {
                        note = name + ": the body lies on its side, the side it named before";
                        return before;
                    }
                    note = name + ": the body lies on its side, nearest";
                    return null;
                }
                flat /= length;
                note = name;
                heldToward = sidePreference == DeathShotSide.BodyRight ? flat : -flat;
                return heldToward;
            }
            default:
                note = "nearest";
                return null;
        }
    }

    private bool IsBlocked(ShotRecipe r, float az, float el, in BodyFrame frame, in BodyPoints points, Vector3[] extra, float ground, float aspect, float userSize)
    {
        var goal = Solve(r, az, el, frame, points, extra, ground, aspect, userSize);
        // The ground lifted the eye far above where the shot wants it (a ledge or a bank on that side).
        if (goal.Elevation > el + 30f * Deg && goal.Elevation > 25f * Deg) return true;
        // The ground there leaves no place from which the shot holds its subject (a body draped over an edge).
        if (goal.Reach > OutOfFrame) return true;
        return Obstructed(r, goal.Centre, goal.Eye, ground, out var hit) && hit < PullInLimit * Vector3.Distance(goal.Centre, goal.Eye);
    }

    /// <summary>The side rule of a shot for a pose: where the eye may sit (azimuth and elevation about the subject).</summary>
    private void AddCandidates(ShotRecipe r, PoseClass pose, in BodyFrame f)
    {
        var lying = PoseClassifier.IsLying(pose);
        var longAz = Azimuth(f.LongAxis);
        var frontAz = Azimuth(f.FrontHorizontal);
        // On a side: which way round from the front the head lies.
        var toHead = AngleDelta(frontAz, longAz) >= 0f ? 1f : -1f;
        switch (r.Profile)
        {
            case DeathShotProfile.LowThreeQuarter:
                if (pose == PoseClass.Side)
                {
                    // On the body's front side, turned toward the head; the feet-ward three-quarter is second choice.
                    Add(r, frontAz + toHead * ShotRecipe.ThreeQuarterAngle, r.Elevation);
                    Add(r, frontAz - toHead * ShotRecipe.ThreeQuarterAngle, r.Elevation, 1);
                }
                else
                {
                    // Upright: either side of the front. Flat on the back or belly: either side of the head end.
                    var reference = lying ? longAz : frontAz;
                    Add(r, reference + ShotRecipe.ThreeQuarterAngle, r.Elevation);
                    Add(r, reference - ShotRecipe.ThreeQuarterAngle, r.Elevation);
                }
                break;
            case DeathShotProfile.LowFace:
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    // In front of the face, turned off it about the body's own axis. A face that points at the
                    // ground has no "in front" above ground: keep turning until the eye clears it, which ends
                    // beside the head, on the profile.
                    var minimum = lying ? 4f * Deg : -90f * Deg;
                    var direction = f.Front;
                    for (var angle = ShotRecipe.FaceOffAngle; angle <= 125f * Deg; angle += 5f * Deg)
                    {
                        direction = f.Front * MathF.Cos(angle) + f.Lateral * (sign * MathF.Sin(angle));
                        if (MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) >= minimum) break;
                    }
                    // Lying with the face turned this way down: there is no such side.
                    if (MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) < minimum) continue;
                    var flat = new Vector3(direction.X, 0f, direction.Z);
                    var az = flat.Length() > 1e-3f ? Azimuth(flat) : frontAz;
                    // Low: never from more than 35° above the face (a face that looks at the sky is seen from beside it).
                    var el = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) + r.Elevation * (1f - MathF.Abs(f.Front.Y));
                    Add(r, az, Math.Clamp(el, -30f * Deg, 35f * Deg));
                }
                break;
            case DeathShotProfile.HipsToFace:
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    // From the chest toward the hips (the torso axis reversed), tipped toward the body's front and a
                    // little to one side: the eye sits by the hips, in front of the belly, and looks up the torso.
                    var direction = -f.Axis * MathF.Cos(ShotRecipe.HipsTip) + f.Front * MathF.Sin(ShotRecipe.HipsTip);
                    direction = Vector3.Normalize(direction * MathF.Cos(ShotRecipe.HipsSide) + f.Lateral * (sign * MathF.Sin(ShotRecipe.HipsSide)));
                    var flat = new Vector3(direction.X, 0f, direction.Z);
                    var az = flat.Length() > 1e-3f ? Azimuth(flat) : frontAz;
                    Add(r, az, Math.Clamp(MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)), -30f * Deg, 45f * Deg));
                }
                break;
            case DeathShotProfile.LowFeetSide:
                if (lying)
                {
                    // Either side of the feet end; on a side, the body's front side first.
                    var feetAz = longAz + MathF.PI;
                    Add(r, feetAz + toHead * ShotRecipe.ThreeQuarterAngle, r.Elevation, 0);
                    Add(r, feetAz - toHead * ShotRecipe.ThreeQuarterAngle, r.Elevation, pose == PoseClass.Side ? 1 : 0);
                }
                else
                {
                    // Upright the feet end is below: from in front, lower and steeper.
                    Add(r, frontAz + ShotRecipe.ThreeQuarterAngle, -40f * Deg);
                    Add(r, frontAz - ShotRecipe.ThreeQuarterAngle, -40f * Deg);
                }
                break;
            case DeathShotProfile.LowBehind:
                if (pose == PoseClass.Side)
                {
                    // From the back side, turned toward the head; toward the feet is second choice.
                    Add(r, frontAz + toHead * ShotRecipe.BehindAngle, r.Elevation);
                    Add(r, frontAz - toHead * ShotRecipe.BehindAngle, r.Elevation, 1);
                }
                else
                {
                    // Upright: 129° off the facing, either side. Flat on the back or belly there is no back side to
                    // stand on: from behind the head, a little off the body's line, looking down it.
                    if (lying)
                    {
                        Add(r, longAz + ShotRecipe.BehindHeadAngle, r.Elevation);
                        Add(r, longAz - ShotRecipe.BehindHeadAngle, r.Elevation);
                    }
                    else
                    {
                        Add(r, frontAz + ShotRecipe.BehindAngle, r.Elevation);
                        Add(r, frontAz - ShotRecipe.BehindAngle, r.Elevation);
                    }
                }
                break;
            case DeathShotProfile.LowFullBody:
                if (lying || f.LongAxisDefined)
                {
                    // From the head end, well off the long axis, so the body runs diagonally across the frame; on a
                    // side, the body's front side first.
                    Add(r, longAz - toHead * ShotRecipe.FullBodyAngle, r.Elevation, 0);
                    Add(r, longAz + toHead * ShotRecipe.FullBodyAngle, r.Elevation, pose == PoseClass.Side ? 1 : 0);
                }
                else
                {
                    Add(r, frontAz + ShotRecipe.ThreeQuarterAngle, r.Elevation);
                    Add(r, frontAz - ShotRecipe.ThreeQuarterAngle, r.Elevation);
                }
                break;
            default:
                // A held body: three-quarter on its front, either side.
                Add(r, frontAz + ShotRecipe.ThreeQuarterAngle, r.Elevation);
                Add(r, frontAz - ShotRecipe.ThreeQuarterAngle, r.Elevation);
                break;
        }
    }

    private void Add(ShotRecipe r, float az, float el, int rank = 0) =>
        candidates.Add(new Candidate { Recipe = r, Azimuth = WrapAngle(az), Elevation = el, Rank = rank });

    // ------------------------------------------------------------------ the solve

    /// <summary>
    /// The undamped shot for a side: look-at point, eye, lens. Distance is closed form: with the view direction
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
            case ShotSubject.ChestToHead:
            {
                var head = p.Head + headAxis * (0.09f * scale);
                // (Nearer the chest than the head: the head is the part that whips about in a fall.)
                centre = Vector3.Lerp(p.Chest, head, 0.2f);
                fit.Add((p.Chest, 0.16f * scale));
                fit.Add((head, 0.14f * scale));
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
