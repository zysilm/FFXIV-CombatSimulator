using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>One camera pose of a run, with what the new solver said about the frame (baselines leave that empty).</summary>
internal struct Cam
{
    public Vector3 Eye, LookAt, BaseEye, GoalEye, GoalLookAt;
    public float Fov;
    public bool Cut, Holding, PulledIn, HasDiagnostics;
    public PoseClass Pose;
    public DeathShotProfile Shot;
    public int SideChanges, SideChangesAfterMove, Fallbacks, Pops;
    /// <summary>On a frame where the side changed: how far the new side is from the old one, degrees.</summary>
    public float SideTurn;
    public bool SideChanged;
    /// <summary>Changes of side so far that were made because the view was blocked.</summary>
    public int BlockedChanges;
    /// <summary>What the side preference did at the last choice of side.</summary>
    public string? SideNote;
    public string? Event;
}

/// <summary>The game camera a death shot starts from: orbiting the chest at 5 m, 15° above, FoV 0.78, at one of
/// eight azimuths.</summary>
internal readonly record struct StartCamera(Vector3 Eye, Vector3 LookAt, float Fov, float Azimuth, float Elevation, float Distance)
{
    public const float DefaultFov = 0.78f;

    public static StartCamera For(Trajectory t, int k)
    {
        var p = t.Points(t.Frames[0].Runtime);
        var azimuth = k * MathF.PI / 4f;
        const float elevation = 15f * MathF.PI / 180f;
        const float distance = 5f;
        return new StartCamera(p.Chest + DeathShotMath.OrbitDirection(azimuth, elevation) * distance, p.Chest, DefaultFov, azimuth, elevation, distance);
    }
}

internal static class Runners
{
    public const float Aspect = 16f / 9f;
    public const int CoreBones = 7;

    /// <summary>The new solver over a trajectory, fed what the plugin would read (bones, then ragdoll bodies).</summary>
    public static Cam[] New(Trajectory t, RecordedWorld world, StartCamera start, DeathShotProfile profile, out float bodyScale, bool oldSourceAtSwitch = true, bool belowGround = false,
        DeathShotSide side = DeathShotSide.Nearest)
    {
        var solver = new DeathShotSolver(world);
        solver.Begin(start.Eye, start.LookAt, start.Fov);
        var cams = new Cam[t.Frames.Length];
        var extra = new Vector3[t.Bones.Length - CoreBones];
        var wasPhysics = false;
        var blockedChanges = 0;
        for (var i = 0; i < t.Frames.Length; i++)
        {
            var f = t.Frames[i];
            Array.Copy(f.Runtime, CoreBones, extra, 0, extra.Length);
            // On the frame the ragdoll starts the plugin can still read the bones as well as the bodies.
            var switching = oldSourceAtSwitch && f.PhysicsStarted && !wasPhysics;
            wasPhysics = f.PhysicsStarted;
            var o = solver.Step(new DeathShotInput
            {
                PreviousSource = switching ? t.Points(f.Bone) : null,
                PreviousSourceExtra = switching ? f.Bone.Skip(CoreBones).ToArray() : null,
                Dt = f.Dt, Points = t.Points(f.Runtime), Extra = extra, PhysicsStarted = f.PhysicsStarted, ExpectRagdoll = true,
                Profile = profile, Aspect = Aspect, UserSize = 1f, AllowBelowGround = belowGround,
                // The face as the plugin reads it: from the head bone of the pose in the skeleton this frame.
                SidePreference = side, Face = f.Face,
            });
            if (o.SideChanged && o.Reason == "blocked") blockedChanges++;
            cams[i] = new Cam
            {
                BlockedChanges = blockedChanges,
                Eye = o.Eye, LookAt = o.LookAt, Fov = o.Fov, Cut = o.Cut, BaseEye = o.BaseEye, GoalEye = o.GoalEye, GoalLookAt = o.GoalLookAt,
                Holding = o.Holding, PulledIn = o.PulledIn, HasDiagnostics = true, Pose = o.Pose, Shot = o.Shot,
                SideChanges = o.SideChanges, SideChangesAfterMove = o.SideChangesAfterMove, Pops = o.TerrainPops, Fallbacks = o.Fallbacks,
                SideChanged = o.SideChanged, SideTurn = o.SideTurn * 180f / MathF.PI,
                Event = o.SideChanged ? $"{f.Time:F1}s {o.Reason} -> {o.Shot} (turn {o.SideTurn * 180f / MathF.PI:F0} deg) [{o.Pose}; {o.SideNote}]" : null,
                SideNote = o.SideNote,
            };
        }
        bodyScale = solver.BodyScale;
        return cams;
    }

    /// <summary>
    /// Baseline 1: the Directed camera's death framing as designed (DynamicCameraController.SolveDeathFraming,
    /// corpse only), with the user's saved values: body band 0.3, angle χ −0.25 (looking up), coverage 1.05, killer
    /// ignored. The yaw is the player's and never changes. Same order as the controller: smoothed ground under the
    /// chest, camera height derived from the band, <see cref="DynamicCameraSolver.GroundedFit"/> over the head-to-feet
    /// anchors, a 2 s translate from the start camera, then the position spring, then the ground floor (0.06 m).
    /// "As designed" = the view is the commanded angle; the game's own aim error (the controller's tilt bias) is not modelled.
    /// </summary>
    public static Cam[] Directed(Trajectory t, RecordedWorld world, StartCamera start)
    {
        const float band0 = 0.3f, chiTarget = -0.25f, coverage = 1.05f, safe = 1f - 0.08f, clearance = 0.06f;
        const float minStandoff = 0.25f, maxDistance = 25f, translate = 2.0f;
        var fov = Math.Clamp(start.Fov, 0.60f, 1.20f);
        var tanV = MathF.Tan(fov * 0.5f);
        var lens = new DynamicCameraSolver.Lens(tanV * Aspect, tanV);
        var yaw = start.Azimuth;
        var dirV = -start.Elevation; // the game's DirV: negative = camera above
        var cams = new Cam[t.Frames.Length];
        var required = new List<Vector3>();
        int kao = t.BoneIndex("j_kao"), chestI = t.BoneIndex("j_sebo_c"), hipsI = t.BoneIndex("j_kosi");
        int[] chain = { t.BoneIndex("j_asi_b_l"), t.BoneIndex("j_asi_b_r"), t.BoneIndex("j_asi_d_l"), t.BoneIndex("j_asi_d_r") };
        bool hasGround = false, hasXRef = false, hasCamGround = false, hasCur = false;
        float groundY = 0f, xRef = 0f, camGroundY = 0f, standoff = 0f, elapsed = 0f;
        var cur = start.Eye;
        var distance = start.Distance;
        var target = start.Eye;
        for (var i = 0; i < t.Frames.Length; i++)
        {
            var f = t.Frames[i];
            var dt = f.Dt;
            var chest = f.Runtime[chestI];
            var hips = f.Runtime[hipsI];
            var head = f.Runtime[kao];
            var dirVDesired = -chiTarget;
            var slew = 0.8f * dt;
            dirV += Math.Clamp(dirVDesired - dirV, -slew, slew);
            var chi = -dirV;

            var ground = world.GroundBelow(chest + new Vector3(0f, 10f, 0f)) ?? chest.Y - 0.25f;
            if (!hasGround) { groundY = ground; hasGround = true; }
            else groundY += (ground - groundY) * (1f - MathF.Exp(-5f * dt));
            var lift = chest.Y - groundY;
            var grounded = Math.Clamp((0.85f - lift) / 0.6f, 0f, 1f);
            var band = Math.Clamp(band0, -0.85f, 0.3f) * grounded;
            var xRaw = Math.Clamp(standoff > 0.1f ? standoff : minStandoff, minStandoff, 4f);
            if (!hasXRef) { xRef = xRaw; hasXRef = true; }
            else xRef += (xRaw - xRef) * (1f - MathF.Exp(-3f * dt));
            var drop = MathF.Tan(MathF.Atan(band * MathF.Abs(lens.TanHalfV)) - chi);
            var height = Math.Clamp(chest.Y - xRef * drop, groundY + clearance, groundY + 3.5f);

            required.Clear();
            required.Add(head);
            var axis = head - chest;
            if (axis.LengthSquared() > 1e-4f) required.Add(head + Vector3.Normalize(axis) * 0.18f);
            required.Add(chest);
            required.Add(hips);
            foreach (var b in chain) required.Add(f.Runtime[b]);
            var floor = groundY + 0.03f;
            for (var k = 0; k < required.Count; k++)
            {
                var p = required[k];
                if (p.Y < floor) p.Y = floor;
                required[k] = new Vector3(chest.X + (p.X - chest.X) * coverage, p.Y, chest.Z + (p.Z - chest.Z) * coverage);
            }
            var fit = DynamicCameraSolver.GroundedFit(required, chest, yaw, chi, in lens, safe, safe, height, minStandoff, maxDistance);
            if (fit.Ok)
            {
                standoff = fit.Standoff;
                target = fit.Camera;
                distance = MathF.Max(0.5f, Vector3.Distance(fit.Camera, chest));
            }
            if (elapsed < translate)
            {
                elapsed += dt;
                var s = DeathShotMath.SmoothStep(elapsed / translate);
                cur = Vector3.Lerp(start.Eye, target, s);
            }
            else
            {
                cur = Vector3.Lerp(cur, target, 1f - MathF.Exp(-6f * dt));
            }
            hasCur = true;
            var camGround = world.GroundBelow(cur + new Vector3(0f, 1f + 10f, 0f));
            if (camGround.HasValue)
            {
                if (!hasCamGround) { camGroundY = camGround.Value; hasCamGround = true; }
                else camGroundY += (camGround.Value - camGroundY) * (1f - MathF.Exp(-5f * dt));
            }
            var safeFloor = hasCamGround ? MathF.Max(groundY, camGroundY) : groundY;
            cur.Y = MathF.Max(cur.Y, safeFloor + clearance);
            DynamicCameraSolver.BasisFromAngles(yaw, chi, out var fwd, out _, out _);
            cams[i] = new Cam { Eye = cur, BaseEye = cur, LookAt = cur + fwd * MathF.Max(0.5f, distance), Fov = fov };
        }
        _ = hasCur;
        return cams;
    }

    /// <summary>A preset of the old Death Cam (removed by aecf654).</summary>
    public readonly record struct OldShot(string Name, float DirH, float DirV, float Distance, float Fov, float Height, float Side, float Transition);

    public static readonly OldShot[] OldShots =
    {
        new("da", -0.808866f, 0.3392281f, 1.31f, 0.63f, 0.16f, 0.04f, 3.9f),
        new("A", -2.25f, 0.44f, 1.35f, 0.65f, 0.10f, 0f, 2.0f),
        new("B", 0.67f, 0.46f, 1.01f, 0.57f, 0.18f, -0.28f, 3.4f),
    };

    /// <summary>
    /// Baseline 2: the old Death Cam formula (git show aecf654^:CombatSimulator/Camera/DeathCamController.cs). The
    /// orbit is relative to the character's root facing: DirH = preset + facing, DirV and distance from the preset;
    /// over the transition they ease (smoothstep, shortest way round) from the camera at death, and the look-at
    /// point eases from where the camera looked to the bone (j_kosi) + height + side offset (across the view); after
    /// that the look-at point is the bone, unsmoothed. The lens is the preset's from the first frame. No ground
    /// check (the user had camera collision off). Game convention: the eye is at look-at + distance ·
    /// (sin H · cos V, −sin V, cos H · cos V), so a positive DirV puts the camera below, looking up.
    /// </summary>
    public static Cam[] OldDeathCam(Trajectory t, StartCamera start, OldShot shot)
    {
        var cams = new Cam[t.Frames.Length];
        var hipsI = t.BoneIndex("j_kosi");
        var startH = start.Azimuth;
        var startV = -start.Elevation;
        var anchorH = shot.DirH + t.Facing;
        var elapsed = 0f;
        for (var i = 0; i < t.Frames.Length; i++)
        {
            var f = t.Frames[i];
            elapsed += f.Dt;
            var s = DeathShotMath.SmoothStep(elapsed / MathF.Max(shot.Transition, 0.01f));
            var h = startH + DeathShotMath.AngleDelta(startH, anchorH) * s;
            var v = startV + (shot.DirV - startV) * s;
            var d = start.Distance + (shot.Distance - start.Distance) * s;
            var a = h - MathF.PI / 2f;
            var desired = f.Runtime[hipsI] + new Vector3(-shot.Side * MathF.Sin(a), shot.Height, -shot.Side * MathF.Cos(a));
            var look = Vector3.Lerp(start.LookAt, desired, s);
            var eye = look + d * new Vector3(MathF.Sin(h) * MathF.Cos(v), -MathF.Sin(v), MathF.Cos(h) * MathF.Cos(v));
            cams[i] = new Cam { Eye = eye, BaseEye = eye, LookAt = look, Fov = shot.Fov };
        }
        return cams;
    }
}
