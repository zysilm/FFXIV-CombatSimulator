// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Stage A7 check: a small hand-made humanoid skeleton, standing on a flat ground plane, made a ragdoll through the
// library's public API only (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §3.1). It must fall, land on the ground, settle,
// and do the same thing twice. Exit code 0 when every check passes.

using System.Numerics;
using AdvancedRagdoll;
using AdvancedRagdoll.Hosting;
using AdvancedRagdoll.SkeletonProfiles;

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures.Add(what);
}

var first = Run(log: true);
var second = Run(log: false);

Console.WriteLine("Checks:");
Check(first.WaitingSteps >= 1, $"waits for the trigger first ({first.WaitingSteps} step(s) waiting)");
Check(first.PhysicsStep > 0, $"physics starts (step {first.PhysicsStep})");
Check(first.StartedCalls == 1, $"OnPhysicsStarted called once ({first.StartedCalls})");
Check(first.FrameCalls > 0, $"OnPhysicsFrame called while physics runs ({first.FrameCalls})");
Check(!first.WroteBeforePhysics, "WritePose writes nothing before physics starts");
Check(first.Finite, "every pose written is finite");
Check(first.StartPelvisY > 0.95f && first.EndPelvisY < 0.35f,
    $"falls: pelvis {first.StartPelvisY:F3} m -> {first.EndPelvisY:F3} m");
Check(first.EndHeadY < 0.45f, $"lies down: head at {first.EndHeadY:F3} m");
Check(first.LowestY > -0.05f, $"stays on the ground: lowest bone {first.LowestY:F3} m");
Check(first.LastSecondMotion < 0.01f, $"settles: largest bone motion in the last second {first.LastSecondMotion * 1000f:F2} mm");
Check(first.RestingStep > 0, $"comes to rest (phase Resting from step {first.RestingStep})");
Check(first.DeactivatedCalls == 1 && first.PhaseAfterDeactivate == RagdollPhase.Inactive,
    $"Deactivate gives the body back (OnDeactivated {first.DeactivatedCalls}, phase {first.PhaseAfterDeactivate})");
Check(first.Fingerprint == second.Fingerprint, $"repeatable: two runs write the same poses ({first.Fingerprint:X16})");

var vanish = RunVanishAndReactivate();
Check(vanish.PhaseAfterVanish == RagdollPhase.Inactive && vanish.DeactivatedAfterVanish == 0,
    $"character vanishes: the ragdoll stops by itself and leaves the body alone (phase {vanish.PhaseAfterVanish}, OnDeactivated {vanish.DeactivatedAfterVanish})");
Check(vanish.RestartStep > 0, $"activated again after vanishing: physics starts again (step {vanish.RestartStep})");
Check(vanish.StartedCalls == 2, $"activated again after vanishing: OnPhysicsStarted fires again ({vanish.StartedCalls} call(s) in all)");
Check(vanish.WroteAfterRestart, "activated again after vanishing: WritePose writes the pose again");

if (failures.Count > 0)
{
    Console.WriteLine($"LibrarySample: {failures.Count} check(s) failed.");
    return 1;
}
Console.WriteLine("LibrarySample: all checks passed.");
return 0;

static Result Run(bool log)
{
    var (names, parents, reference) = Humanoid();
    var skeleton = new ArraySkeleton(names, parents, reference);
    // Standing on the ground, leaning a little forward so the fall has a direction.
    skeleton.SetRoot(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.12f), Vector3.One);
    var actor = new SampleActor(new ActorId(42));
    var host = new RagdollHost
    {
        Skeleton = skeleton,
        World = DelegateWorldQuery.FlatGround(0f),
        Actor = actor,
        Log = log ? new ConsoleLog() : NullRagdollLog.Instance,
    };

    using var ragdoll = new Ragdoll(host, SkeletonProfile.Auto());
    var result = new Result { StartPelvisY = skeleton.GetWorldPosition(0).Y, Finite = true };
    ragdoll.Activate(new RagdollActivation(RagdollTriggerMode.FixedDelay, Delay: 0.05f));

    const int steps = 600; // ten seconds at 60 Hz
    var previous = new Vector3[skeleton.BoneCount];
    var hash = 14695981039346656037UL;
    for (var step = 1; step <= steps; step++)
    {
        var phase = ragdoll.Step(1f / 60f);
        var wrote = ragdoll.WritePose();
        if (phase == RagdollPhase.Waiting) { result.WaitingSteps++; if (wrote) result.WroteBeforePhysics = true; }
        if (phase is RagdollPhase.Running or RagdollPhase.Resting && result.PhysicsStep == 0) result.PhysicsStep = step;
        if (phase == RagdollPhase.Resting && result.RestingStep == 0) result.RestingStep = step;

        var motion = 0f;
        for (var b = 0; b < skeleton.BoneCount; b++)
        {
            var p = skeleton.GetWorldPosition(b);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) result.Finite = false;
            if (wrote) result.LowestY = MathF.Min(result.LowestY, p.Y);
            if (step > 1) motion = MathF.Max(motion, Vector3.Distance(p, previous[b]));
            previous[b] = p;
            foreach (var f in new[] { p.X, p.Y, p.Z })
                hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(f)) * 1099511628211UL;
        }
        if (step > steps - 60) result.LastSecondMotion = MathF.Max(result.LastSecondMotion, motion);
    }

    result.EndPelvisY = skeleton.GetWorldPosition(0).Y;
    result.EndHeadY = skeleton.GetWorldPosition(Array.IndexOf(names, "head")).Y;
    result.Fingerprint = hash;
    result.StartedCalls = actor.Started;
    result.FrameCalls = actor.Frames;
    ragdoll.Deactivate();
    result.PhaseAfterDeactivate = ragdoll.Phase;
    result.DeactivatedCalls = actor.Deactivated;

    if (log)
        Console.WriteLine($"Run: physics from step {result.PhysicsStep}, resting from step {result.RestingStep}, " +
                          $"pelvis {result.StartPelvisY:F3} -> {result.EndPelvisY:F3} m, head {result.EndHeadY:F3} m, lowest {result.LowestY:F3} m");
    return result;
}

// The character vanishes (IRagdollActor.Exists turns false) while physics runs; the ragdoll stops by itself. Then the
// same character is back and the same Ragdoll is activated again: physics must start again, with OnPhysicsStarted.
static VanishResult RunVanishAndReactivate()
{
    var (names, parents, reference) = Humanoid();
    var skeleton = new ArraySkeleton(names, parents, reference);
    skeleton.SetRoot(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.12f), Vector3.One);
    var actor = new SampleActor(new ActorId(7));
    var host = new RagdollHost { Skeleton = skeleton, World = DelegateWorldQuery.FlatGround(0f), Actor = actor };
    using var ragdoll = new Ragdoll(host, SkeletonProfile.Auto());
    var result = new VanishResult();

    ragdoll.Activate(new RagdollActivation(RagdollTriggerMode.FixedDelay, Delay: 0.05f));
    for (var step = 0; step < 60; step++) { ragdoll.Step(1f / 60f); ragdoll.WritePose(); }
    actor.Present = false;
    for (var step = 0; step < 5; step++) { ragdoll.Step(1f / 60f); ragdoll.WritePose(); }
    result.PhaseAfterVanish = ragdoll.Phase;
    result.DeactivatedAfterVanish = actor.Deactivated;

    actor.Present = true;
    ragdoll.Activate(new RagdollActivation(RagdollTriggerMode.FixedDelay, Delay: 0.05f));
    for (var step = 1; step <= 60; step++)
    {
        var phase = ragdoll.Step(1f / 60f);
        var wrote = ragdoll.WritePose();
        if (phase is RagdollPhase.Running or RagdollPhase.Resting && result.RestartStep == 0) result.RestartStep = step;
        result.WroteAfterRestart |= wrote;
    }
    result.StartedCalls = actor.Started;
    return result;
}

// A 22-bone humanoid, about 1.75 m tall, in a parent-local reference pose (Y up, facing +Z). Tip bones (head_end,
// hand_end, toe_end) give the head, hands and toes a segment of their own, so they get bodies too.
static (string[] Names, short[] Parents, BoneTransform[] Reference) Humanoid()
{
    var bones = new (string Name, string? Parent, Vector3 Offset)[]
    {
        ("pelvis", null, new(0f, 1.00f, 0f)),
        ("spine", "pelvis", new(0f, 0.12f, 0f)),
        ("chest", "spine", new(0f, 0.20f, 0f)),
        ("neck", "chest", new(0f, 0.20f, 0f)),
        ("head", "neck", new(0f, 0.10f, 0f)),
        ("head_end", "head", new(0f, 0.20f, 0f)),
        ("upperarm_l", "chest", new(0.20f, 0.15f, 0f)),
        ("forearm_l", "upperarm_l", new(0f, -0.30f, 0f)),
        ("hand_l", "forearm_l", new(0f, -0.26f, 0f)),
        ("hand_end_l", "hand_l", new(0f, -0.15f, 0f)),
        ("upperarm_r", "chest", new(-0.20f, 0.15f, 0f)),
        ("forearm_r", "upperarm_r", new(0f, -0.30f, 0f)),
        ("hand_r", "forearm_r", new(0f, -0.26f, 0f)),
        ("hand_end_r", "hand_r", new(0f, -0.15f, 0f)),
        ("thigh_l", "pelvis", new(0.10f, -0.05f, 0f)),
        ("shin_l", "thigh_l", new(0f, -0.45f, 0f)),
        ("foot_l", "shin_l", new(0f, -0.42f, 0f)),
        ("toe_end_l", "foot_l", new(0f, -0.06f, 0.16f)),
        ("thigh_r", "pelvis", new(-0.10f, -0.05f, 0f)),
        ("shin_r", "thigh_r", new(0f, -0.45f, 0f)),
        ("foot_r", "shin_r", new(0f, -0.42f, 0f)),
    };
    var names = bones.Select(b => b.Name).Append("toe_end_r").ToArray();
    var parents = new short[names.Length];
    var reference = new BoneTransform[names.Length];
    for (var i = 0; i < bones.Length; i++)
    {
        parents[i] = bones[i].Parent == null ? (short)-1 : (short)Array.IndexOf(names, bones[i].Parent);
        reference[i] = new BoneTransform(bones[i].Offset, Quaternion.Identity, Vector3.One);
    }
    parents[^1] = (short)Array.IndexOf(names, "foot_r");
    reference[^1] = new BoneTransform(new Vector3(0f, -0.06f, 0.16f), Quaternion.Identity, Vector3.One);
    return (names, parents, reference);
}

sealed class Result
{
    public int WaitingSteps, PhysicsStep, RestingStep, StartedCalls, FrameCalls, DeactivatedCalls;
    public bool WroteBeforePhysics, Finite;
    public float StartPelvisY, EndPelvisY, EndHeadY, LowestY = float.MaxValue, LastSecondMotion;
    public ulong Fingerprint;
    public RagdollPhase PhaseAfterDeactivate;
}

sealed class VanishResult
{
    public RagdollPhase PhaseAfterVanish;
    public int DeactivatedAfterVanish, RestartStep, StartedCalls;
    public bool WroteAfterRestart;
}

sealed class SampleActor(ActorId id) : IRagdollActor
{
    public int Started, Frames, Deactivated;
    public bool Present = true;
    public ActorId Id { get; } = id;
    public bool Exists => Present;
    public RagdollFidelity Fidelity => RagdollFidelity.Primary;
    public float FacingYaw => 0f;
    public bool TryGetAnimationSample(out AnimationSample sample) { sample = default; return false; }
    public void OnPhysicsStarted() => Started++;
    public void OnPhysicsFrame(Vector3 rootBodyPosition) => Frames++;
    public void OnDeactivated() => Deactivated++;
}

sealed class ConsoleLog : IRagdollLog
{
    public void Info(string message) { }
    public void Warning(string message) => Console.WriteLine("  warning: " + message);
    public void Warning(Exception exception, string message) => Console.WriteLine($"  warning: {message} ({exception.Message})");
    public void Error(string message) => Console.WriteLine("  error: " + message);
    public void Error(Exception exception, string message) => Console.WriteLine($"  error: {message} ({exception})");
}
