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
}

/// <summary>What the solver hands back each frame: the pose for the game camera (eye and look-at through the pose
/// override, the vertical FoV through the coordinator) and what it decided, for the log and the offline check.</summary>
public struct DeathShotOutput
{
    public Vector3 Eye, LookAt;
    /// <summary>Vertical field of view, radians.</summary>
    public float Fov;
    /// <summary>This frame is a cut: the pose jumped on purpose.</summary>
    public bool Cut;
    public PoseClass Pose;
    /// <summary>The shot in use (never Auto).</summary>
    public DeathShotProfile Shot;
    /// <summary>The undamped solve of this frame.</summary>
    public Vector3 GoalEye, GoalLookAt;
    public float GoalFov;
    /// <summary>The damped pose before the built-in motion (push-in, arc) is added.</summary>
    public Vector3 BaseEye, BaseLookAt;
    public bool BodySettled;
    /// <summary>The body is settled and the shot holds its solve (the built-in motion may run).</summary>
    public bool Holding;
    /// <summary>The side or the shot was changed this frame (not counting the first choice).</summary>
    public bool SideChanged;
    /// <summary>Changes of side so far; of those, how many because a settled body was moved and came to rest turned.</summary>
    public int SideChanges, SideChangesAfterMove;
    public int Cuts, Fallbacks;
    /// <summary>Times the eye was found inside the terrain and had to be put on top of it (each is a cut frame).</summary>
    public int TerrainPops;
    /// <summary>The eye was pulled in (and the lens widened) this frame because something stood in the way.</summary>
    public bool PulledIn;
    /// <summary>The position source changed this frame (ragdoll start); <see cref="SourceJump"/> is how far the
    /// hips moved because of it, before the solver absorbed it.</summary>
    public bool SourceSwitched;
    public float SourceJump;
    /// <summary>Why the side was last chosen: start, settled, moved, calm, blocked, profile.</summary>
    public string Reason;
}

/// <summary>
/// The automatic death shot: every frame, from bone positions alone, a camera pose that frames the fallen body.
/// Game-free and deterministic; the plugin feeds it bones or ragdoll bodies and writes its output to the camera,
/// Tests/DeathShots feeds it recorded trajectories. Design and numbers: docs/DEATH_SHOTS.md.
///
/// Per frame: body frame and pose class → side choice (only at the start, once when the body has settled, and when
/// it settles again after being moved) → distance (closed form: the smallest that keeps the shot's points inside
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
    public const float KeepSideAtSettle = 25f * Deg;
    public const float LargePoseChange = 60f * Deg;
    public const float EaseUpTo = 35f * Deg;
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

    private readonly IDeathShotWorld world;
    private readonly PoseClassifier classifier = new();
    private readonly SettleDetector settle = new();
    private readonly SettleDetector calm = new(CalmSpeed, CalmSeconds);
    private readonly List<(Vector3 P, float R)> fit = new();
    private readonly List<Candidate> candidates = new();

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
    private int sideChanges, sideChangesAfterMove, cuts, fallbacks, pops;
    private float rampAge;
    private string reason = "start";
    private DeathShotProfile lastProfile;

    // hold
    private bool holding;
    private float holdFor;
    private BodyPoints heldPoints;
    private Vector3[] heldExtra = Array.Empty<Vector3>();
    private float heldGround;
    private float motion, motionVelocity, arcSign = 1f, motionAmount = 1f;

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
        sideChanges = cuts = fallbacks = pops = 0;
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

    private struct Candidate
    {
        public ShotRecipe Recipe;
        public float Azimuth, Elevation;
        /// <summary>0 = what the side rule asks for; 1 = its second choice.</summary>
        public int Rank;
    }

    public DeathShotOutput Step(in DeathShotInput input)
    {
        var dt = Math.Clamp(input.Dt, 0f, 0.1f);
        var aspect = input.Aspect > 0.1f ? input.Aspect : 16f / 9f;
        var userSize = input.UserSize > 0.01f ? Math.Clamp(input.UserSize, 0.5f, 3f) : 1f;
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
        var changed = false;
        var cut = false;
        if (!has)
        {
            lastProfile = input.Profile;
            var startAzimuth = hasStart ? Azimuth(startEye - startLook) : Azimuth(subjectFrame.FrontHorizontal);
            Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, startAzimuth, float.MaxValue, "start");
        }
        else if (input.Profile != lastProfile)
        {
            // The dropdown changed while dead: switch at once.
            lastProfile = input.Profile;
            (changed, cut) = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, 0f, "profile");
        }
        else if (settled && !wasSettled)
        {
            // Once, when the body has come to rest. A body that settled before and was moved is only given another
            // side for a large pose change; a side already re-chosen on the way here (calm, blocked) stays unless it
            // cannot (blocked, or a shot the pose rules out).
            var moved = firstSettleDone;
            var keep = moved ? LargePoseChange : lateDone ? MathF.PI : KeepSideAtSettle;
            (changed, cut) = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, keep,
                moved ? "moved" : "settled");

            firstSettleDone = true;
        }
        else if (calmNow && !settled && !firstSettleDone && !lateDone)
        {
            // A body that stays put but will not lie still (trampled, twitching): look once anyway.
            lateDone = true;
            (changed, cut) = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, KeepSideAtSettle, "calm");
        }
        wasSettled = settled;

        // ---- 4. distance and constraints: this frame's goal ----
        var goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
        var pulled = false;
        if (world.Blocked(goal.Centre, goal.Eye, out var hit))
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
                if (blockedFor > BlockedSeconds && has)
                {
                    blockedFor = 0f;
                    lateDone = true;
                    var (c2, k2) = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, 0f, "blocked");
                    changed |= c2;
                    cut |= k2;
                    goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
                }
            }
        }
        else if (goal.Reach > OutOfFrame)
        {
            // Nothing in the way, but the ground leaves the subject outside the frame from here: the same remedy.
            blockedFor += dt;
            if (blockedFor > BlockedSeconds && has)
            {
                blockedFor = 0f;
                lateDone = true;
                var (c2, k2) = Choose(input.Profile, pose, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize, azimuth, 0f, "blocked");
                changed |= c2;
                cut |= k2;
                goal = Solve(recipe, lockAzimuth + input.UserYaw, lockElevation, subjectFrame, subjectPoints, subjectExtra, subjectGround, aspect, userSize);
            }
        }
        else
        {
            blockedFor = 0f;
        }

        // ---- 5. damping ----
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
                cut = true;
            }
            heldLook = goal.LookAt;
            heldDistance = goal.Distance;
        }
        else if (cut)
        {
            Snap(goal);
            heldLook = goal.LookAt;
            heldDistance = goal.Distance;
            motion = motionVelocity = 0f;
            holdFor = 0f;
        }
        else
        {
            // Dead zone: the goal has to leave a 3 cm ball before the camera answers.
            var away = goal.LookAt - heldLook;
            var far = away.Length();
            if (holding) heldLook = goal.LookAt; // a body at rest is framed exactly
            else if (far > DeadZone) heldLook += away * ((far - DeadZone) / far);
            var dd = goal.Distance - heldDistance;
            if (holding) heldDistance = goal.Distance;
            else if (MathF.Abs(dd) > DeadZone) heldDistance += dd - MathF.Sign(dd) * DeadZone;

            // The entry eases in on slower springs; a source switch doubles the damping for half a second.
            var entry = hasStart ? 1f - SmoothStep((time - (EntrySeconds - 0.5f)) / 1.0f) : 0f;
            // (The doubling comes in and goes out smoothly: a step in the spring constants would itself be felt.)
            var sinceSwitch = switchAge - dt; // 0 on the frame of the switch itself
            var bump = sinceSwitch < SwitchSeconds ? MathF.Sin(MathF.PI * MathF.Max(0f, sinceSwitch) / SwitchSeconds) : 0f;
            var boost = 1f + bump * bump;
            var lookTime = (LookSmoothTime + (EntryLookSmoothTime - LookSmoothTime) * entry) * boost;
            var eyeTime = (EyeSmoothTime + (EntryEyeSmoothTime - EyeSmoothTime) * entry) * boost;

            // The speed caps open over the first 0.6 s after the start and after a change of side, so a far goal
            // is not answered with a jolt (the acceleration is bounded by cap / 0.6 s).
            if (changed) rampAge = 0f;
            rampAge += dt;
            var ramp = Math.Clamp(rampAge / RampSeconds, 0.02f, 1f);
            look = Spring(look, heldLook, ref lookVelocity, lookTime, MaxLookSpeed, dt);
            // Angles: capped so the view turns under 60°/s and the eye stays under 3 m/s on a long orbit.
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
            var want = t <= 0f ? 0f : recipe.Motion == ShotMotion.PushIn
                ? SmoothStep(t / ShotRecipe.PushInSeconds)
                : SmoothStep(t / ShotRecipe.ArcSeconds);
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
        if (recipe.Motion == ShotMotion.PushIn) outDistance *= 1f - ShotRecipe.PushInAmount * motion;
        else outAzimuth += arcSign * ShotRecipe.ArcAngle * motion;

        // ---- 7. the pose, kept off the ground and the water, and under the view-rate cap ----
        var soft = recipe.EyeHeight <= 0f;
        var baseEye = soft ? Land(look + OrbitDirection(azimuth, elevation) * distance) : Floor(look + OrbitDirection(azimuth, elevation) * distance);
        var eye = look + OrbitDirection(outAzimuth, elevation) * outDistance;
        if (soft) eye = Land(eye);
        var lookAt = look;
        var popped = false;
        eye = hasOutput && !cut && dt > 0f ? Advance(lastEye, eye, dt, out popped) : Floor(eye);
        if (popped)
        {
            // The eye turned out to be inside the terrain and was put on top of it: a jump, and reported as one.
            cut = true;
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
        if (hasOutput && !cut && has && dt > 0f)
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
        if (cut && changed) cuts++;

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
        output.Cuts = cuts;
        output.Fallbacks = fallbacks;
        output.TerrainPops = pops;
        output.PulledIn = pulled;
        output.Reason = reason;
        return output;
    }

    /// <summary>The built-in motion must not carry the eye behind something or the subject out of the frame: an arc
    /// goes to the side whose end is clear, a motion that would lose the subject is halved, then dropped.</summary>
    private void PlanMotion(in Goal goal, float aspect)
    {
        var tanV = MathF.Tan(goal.Fov * 0.5f);
        var tanH = tanV * aspect;
        foreach (var amount in stackalloc[] { 1f, 0.5f })
        {
            if (recipe.Motion == ShotMotion.PushIn)
            {
                var end = goal.LookAt + (goal.Eye - goal.LookAt) * (1f - ShotRecipe.PushInAmount * amount);
                if (ReachOf(end, goal.LookAt, tanH, tanV) <= 1f) { motionAmount = amount; return; }
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var end = Floor(goal.LookAt + OrbitDirection(goal.Azimuth + arcSign * ShotRecipe.ArcAngle * amount, goal.Elevation) * goal.Distance);
                if (!world.Blocked(goal.Centre, end, out _) && ReachOf(end, goal.LookAt, tanH, tanV) <= 1f) { motionAmount = amount; return; }
                arcSign = -arcSign;
            }
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
    /// </summary>
    private Vector3 Advance(Vector3 from, Vector3 wanted, float dt, out bool popped)
    {
        popped = false;
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
    /// Picks the shot and the side. The candidates of the profile's side rule are tried nearest-first from
    /// <paramref name="fromAzimuth"/>; one whose view of the subject is blocked is passed over, then the azimuth
    /// is stepped ±15° up to ±90°, then Overhead. With <paramref name="keepWithin"/> the present side stays when it
    /// is still within that angle of what the rule asks. Returns (changed, cut).
    /// </summary>
    private (bool Changed, bool Cut) Choose(DeathShotProfile profile, PoseClass pose, in BodyFrame frame, in BodyPoints points,
        Vector3[] extra, float ground, float aspect, float userSize, float fromAzimuth, float keepWithin, string why)
    {
        candidates.Clear();
        if (profile == DeathShotProfile.Auto)
            foreach (var r in ShotRecipe.ForAuto(pose)) AddCandidates(r, pose, frame);
        else
            AddCandidates(ShotRecipe.Of(profile), pose, frame);
        candidates.Sort((a, b) => a.Rank != b.Rank
            ? a.Rank.CompareTo(b.Rank)
            : MathF.Abs(AngleDelta(fromAzimuth, a.Azimuth)).CompareTo(MathF.Abs(AngleDelta(fromAzimuth, b.Azimuth))));

        var first = keepWithin == float.MaxValue;
        if (!first && keepWithin > 0f)
        {
            // Keep the side when the shot in use is still allowed and still close to what its rule asks.
            if (keepWithin >= MathF.PI && (recipe != ShotRecipe.Face || pose != PoseClass.Prone) &&
                !IsBlocked(recipe, lockAzimuth, lockElevation, frame, points, extra, ground, aspect, userSize))
                return (false, false);
            foreach (var c in candidates)
            {
                if (c.Recipe != recipe || c.Rank != 0) continue;
                if (MathF.Abs(AngleDelta(lockAzimuth, c.Azimuth)) <= keepWithin && MathF.Abs(lockElevation - c.Elevation) <= keepWithin &&
                    !IsBlocked(recipe, lockAzimuth, lockElevation, frame, points, extra, ground, aspect, userSize))
                    return (false, false);
            }
        }

        Candidate? pick = null;
        foreach (var c in candidates)
        {
            if (IsBlocked(c.Recipe, c.Azimuth, c.Elevation, frame, points, extra, ground, aspect, userSize)) continue;
            pick = c;
            break;
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
            if (pick == null && best.Recipe != ShotRecipe.Overhead)
            {
                var count = candidates.Count;
                AddCandidates(ShotRecipe.Overhead, pose, frame);
                for (var i = count; i < candidates.Count && pick == null; i++)
                    if (!IsBlocked(candidates[i].Recipe, candidates[i].Azimuth, candidates[i].Elevation, frame, points, extra, ground, aspect, userSize))
                        pick = candidates[i];
            }
            if (pick != null) fallbacks++;
            pick ??= best;
        }
        if (pick == null) return (false, false);

        var chosen = pick.Value;
        reason = why;
        if (first)
        {
            recipe = chosen.Recipe;
            lockAzimuth = chosen.Azimuth;
            lockElevation = chosen.Elevation;
            arcSign = AngleDelta(lockAzimuth, Azimuth(frame.LongAxis)) >= 0f ? 1f : -1f;
            return (false, false);
        }
        var turn = MathF.Acos(Math.Clamp(Vector3.Dot(OrbitDirection(lockAzimuth, lockElevation), OrbitDirection(chosen.Azimuth, chosen.Elevation)), -1f, 1f));
        var same = chosen.Recipe == recipe && turn < 1f * Deg;
        var bigTurn = turn > EaseUpTo;
        recipe = chosen.Recipe;
        lockAzimuth = chosen.Azimuth;
        lockElevation = chosen.Elevation;
        arcSign = AngleDelta(lockAzimuth, Azimuth(frame.LongAxis)) >= 0f ? 1f : -1f;
        // A change of side is a cut or a short ease, never a long sweep around the body.
        return same ? (false, false) : (true, bigTurn);
    }

    private bool IsBlocked(ShotRecipe r, float az, float el, in BodyFrame frame, in BodyPoints points, Vector3[] extra, float ground, float aspect, float userSize)
    {
        var goal = Solve(r, az, el, frame, points, extra, ground, aspect, userSize);
        // The ground lifted the eye far above where the shot wants it (a ledge or a bank on that side).
        if (goal.Elevation > el + 30f * Deg && goal.Elevation > 25f * Deg) return true;
        // The ground there leaves no place from which the shot holds its subject (a body draped over an edge).
        if (goal.Reach > OutOfFrame) return true;
        return world.Blocked(goal.Centre, goal.Eye, out var hit) && hit < PullInLimit * Vector3.Distance(goal.Centre, goal.Eye);
    }

    /// <summary>The side rule of a shot for a pose: where the eye may sit (azimuth and elevation about the subject).</summary>
    private void AddCandidates(ShotRecipe r, PoseClass pose, in BodyFrame f)
    {
        var lying = PoseClassifier.IsLying(pose);
        var longAz = Azimuth(f.LongAxis);
        var frontAz = Azimuth(f.FrontHorizontal);
        switch (r.Profile)
        {
            case DeathShotProfile.LowThreeQuarter:
                if (pose == PoseClass.Side)
                {
                    // On the body's front side, turned toward the head; the feet-ward three-quarter is second choice.
                    var toHead = AngleDelta(frontAz, longAz) >= 0f ? 1f : -1f;
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
            case DeathShotProfile.Face:
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
                    var el = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) + r.Elevation * (1f - MathF.Abs(f.Front.Y));
                    Add(r, az, Math.Clamp(el, -30f * Deg, 65f * Deg));
                }
                break;
            case DeathShotProfile.AlongBody:
                // Beyond the feet, looking toward the head. Upright there is no "beyond the feet": from the front.
                Add(r, lying || f.LongAxisDefined ? longAz + MathF.PI : frontAz, r.Elevation);
                break;
            default:
                // Overhead, Ground profile: across the long axis, from either side.
                Add(r, longAz + MathF.PI / 2f, r.Elevation);
                Add(r, longAz - MathF.PI / 2f, r.Elevation);
                break;
        }
    }

    private void Add(ShotRecipe r, float az, float el, int rank = 0) =>
        candidates.Add(new Candidate { Recipe = r, Azimuth = WrapAngle(az), Elevation = el, Rank = rank });

    // ------------------------------------------------------------------ the solve

    /// <summary>
    /// The undamped shot for a side: look-at point, eye, lens. Distance is closed form: with the view direction
    /// fixed, every point p asks for a retreat of at least (|x| + r) / (safe · tanHalf) − z along the view axis (x
    /// across, z along, from the subject's centre), and the largest request wins. A shot pinned above the ground
    /// retreats horizontally instead; the same inequalities are linear in that retreat too.
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

        if (r.EyeHeight <= 0f)
        {
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
                var floor = FloorAt(eye);
                if (eye.Y >= floor - 1e-4f) break;
                elevationNow = MathF.Asin(Math.Clamp((floor - centre.Y) / d, -1f, 1f));
            }
            goal.LookAt = centre;
            goal.Eye = Floor(eye);
            FromEye(ref goal);
            goal.Reach = ReachOf(goal.Eye, goal.LookAt, tanH, tanV);
            return goal;
        }

        // Pinned above the ground: the eye backs away horizontally along the azimuth at its height. A fixed pitch
        // suits level ground; where the ground under the eye stands well above or below the body (a slope, a
        // ledge) it can leave the body outside the frame, and the shot aims at the body instead.
        var pinned = Pinned(r, az, el, r.AimAtSubject, centre, ground, least, most, userSize, kx, ky, tanH, tanV, out var reach);
        if (!r.AimAtSubject && reach > 1f)
        {
            var aimed = Pinned(r, az, el, true, centre, ground, least, most, userSize, kx, ky, tanH, tanV, out var aimedReach);
            if (aimedReach < reach) pinned = aimed;
        }
        pinned.Fov = fovNow;
        pinned.Centre = centre;
        pinned.Reach = ReachOf(pinned.Eye, pinned.LookAt, tanH, tanV);
        return pinned;
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

    private Goal Pinned(ShotRecipe r, float az, float el, bool aim, Vector3 centre, float ground, float least, float most, float userSize,
        float kx, float ky, float tanH, float tanV, out float reach)
    {
        var goal = new Goal();
        var back = Horizontal(az);
        var pitch = el;
        var lateral = 0f;
        var t = least * userSize;
        var height = MathF.Max(r.EyeHeight * scale, GroundClearance);
        float HeightAt(Vector3 at)
        {
            var y = FloorAt(at) - GroundClearance + height;
            return float.IsFinite(y) ? y : ground + height;
        }
        if (aim)
        {
            // Aimed at the subject, the view direction turns as the eye backs away, so the inequalities are no
            // longer linear in the retreat: the nearest retreat that fits is found by stepping out and bisecting.
            Vector3 EyeAt(float retreat)
            {
                var at = centre + back * retreat;
                at.Y = HeightAt(at);
                return at;
            }
            float ReachAt(float retreat, out Vector3 at, out Vector3 look)
            {
                at = EyeAt(retreat);
                look = -OrbitDirection(az, MathF.Atan2(at.Y - centre.Y, MathF.Max(0.2f, retreat)));
                DynamicCameraSolver.BasisFromForward(look, out var rightNow, out var upNow);
                var most2 = 0f;
                foreach (var (point, radius) in fit)
                {
                    var v = point - at;
                    var z = Vector3.Dot(v, look);
                    if (z < 0.3f) return float.MaxValue;
                    most2 = MathF.Max(most2, MathF.Max((MathF.Abs(Vector3.Dot(v, rightNow)) + radius) / (z * kx), (MathF.Abs(Vector3.Dot(v, upNow)) + radius) / (z * ky)));
                }
                return most2;
            }
            float lo = least, hi = least;
            if (ReachAt(least, out _, out _) > 1f)
            {
                var found = false;
                for (var i = 0; i < 16 && hi < most; i++)
                {
                    lo = hi;
                    hi = MathF.Min(hi * 1.3f + 0.15f, most);
                    if (ReachAt(hi, out _, out _) <= 1f) { found = true; break; }
                }
                if (found)
                    for (var i = 0; i < 10; i++)
                    {
                        var mid = (lo + hi) * 0.5f;
                        if (ReachAt(mid, out _, out _) <= 1f) hi = mid; else lo = mid;
                    }
            }
            var retreatNow = MathF.Min(hi * userSize, most);
            ReachAt(retreatNow, out var aimedEye, out var aimedFwd);
            goal.Eye = Floor(aimedEye);
            reach = ReachOf(goal.Eye, goal.Eye + aimedFwd, tanH, tanV);
            goal.LookAt = goal.Eye + aimedFwd * MathF.Max(0.3f, Vector3.Dot(centre - goal.Eye, aimedFwd));
            FromEye(ref goal);
            return goal;
        }
        var eye = centre + back * t;
        eye.Y = HeightAt(eye);
        Vector3 fwd = -back, right = default, up = Vector3.UnitY;
        for (var pass = 0; pass < 5; pass++)
        {
            fwd = -OrbitDirection(az, pitch);
            DynamicCameraSolver.BasisFromForward(fwd, out right, out up);
            var origin = new Vector3(centre.X, eye.Y, centre.Z) + right * lateral;
            var c = -Vector3.Dot(back, fwd);
            var e = Vector3.Dot(back, up);
            var need = least;
            foreach (var (point, radius) in fit)
            {
                var v = point - origin;
                var z = Vector3.Dot(v, fwd);
                var x = Vector3.Dot(v, right);
                var y = Vector3.Dot(v, up);
                if (c > 1e-3f)
                {
                    need = MathF.Max(need, ((MathF.Abs(x) + radius) / kx - z) / c);
                    need = MathF.Max(need, (0.3f - z) / c);
                }
                if (ky * c + e > 1e-3f) need = MathF.Max(need, (y + radius - ky * z) / (ky * c + e));
                if (ky * c - e > 1e-3f) need = MathF.Max(need, (-y + radius - ky * z) / (ky * c - e));
            }
            t = MathF.Min(need * userSize, most);
            eye = origin + back * t;
            eye.Y = HeightAt(eye);
            // Centre the subject across the frame, then let the next pass shrink the retreat into the room gained.
            float minX = float.MaxValue, maxX = float.MinValue, depthMin = 1f, depthMax = 1f;
            foreach (var (point, _) in fit)
            {
                var v = point - eye;
                var z = MathF.Max(0.05f, Vector3.Dot(v, fwd));
                var ndc = Vector3.Dot(v, right) / (z * tanH);
                if (ndc < minX) { minX = ndc; depthMin = z; }
                if (ndc > maxX) { maxX = ndc; depthMax = z; }
            }
            var middle = (minX + maxX) * 0.5f;
            if (pass < 4 && MathF.Abs(middle) > 0.02f)
            {
                var k = 0.5f * (1f / (depthMin * tanH) + 1f / (depthMax * tanH));
                lateral += middle / k;
                eye += right * (middle / k);
                eye.Y = HeightAt(eye);
            }
        }
        goal.Eye = Floor(eye);
        // How far the points reach toward the frame edge with the eye where it ended (1 = the edge).
        reach = 0f;
        foreach (var (point, _) in fit)
        {
            var v = point - goal.Eye;
            var z = Vector3.Dot(v, fwd);
            if (z < 0.05f) { reach = float.MaxValue; break; }
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(Vector3.Dot(v, right)) / (z * tanH), MathF.Abs(Vector3.Dot(v, up)) / (z * tanV)));
        }
        var depth = MathF.Max(0.3f, Vector3.Dot(centre - goal.Eye, fwd));
        goal.LookAt = goal.Eye + fwd * depth;
        FromEye(ref goal);
        return goal;
    }
}
