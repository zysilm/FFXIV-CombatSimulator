// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

/// <summary>
/// Virtual enemies keep their head on their current target (the player or a companion).
/// The game's own look-at cannot be used: a virtual enemy is a clone of the local player and
/// targeting that player does not drive its head. Instead a chosen bone (default head) is
/// turned in ModelPose each render frame, layered on top of the animation and limited in
/// range, and the head's face/hair partial skeletons follow it.
/// </summary>
public sealed unsafe class VirtualEnemyLookAtController : IDisposable
{
    private const float MaxYaw = 70f * MathF.PI / 180f;
    private const float MaxPitch = 35f * MathF.PI / 180f;
    private const float TurnRate = 8f;   // 1/s, toward the target swing
    private const float ReleaseRate = 5f; // 1/s, back to the animation when not looking

    private readonly BoneTransformService bones;
    private readonly Configuration config;
    private readonly NpcSpawner spawner;
    private readonly CombatEngine engine;
    private readonly Func<nint, bool> excluded;
    private readonly IPluginLog log;

    private readonly record struct Pair(LookAtActor Enemy, LookAtActor Target);
    private sealed record FrameState(Pair[] Pairs, HashSet<LookAtActor> Living);
    private static readonly FrameState Empty = new(Array.Empty<Pair>(), new());
    // Publish target pairs and release eligibility atomically; readers never mix two ticks.
    private volatile FrameState frame = Empty;
    private bool wasRunning;
    private long releaseUntil;
    private readonly Dictionary<int, Quaternion> delta = new(1);
    private readonly Dictionary<LookAtActor, Quaternion> swings = new();
    private readonly List<LookAtActor> released = new();
    private long lastRender;

    public VirtualEnemyLookAtController(BoneTransformService bones, Configuration config, NpcSpawner spawner,
        CombatEngine engine, Func<nint, bool> excluded, IPluginLog log)
    {
        this.bones = bones; this.config = config; this.spawner = spawner;
        this.engine = engine; this.excluded = excluded; this.log = log;
        Core.Services.Framework.Update += OnFrameworkUpdate;
        bones.OnFinalPoseModifiers += OnRenderFrame;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        bool running = config.VirtualEnemyLookAtTarget && engine.IsActive;
        if (!running && wasRunning) releaseUntil = Environment.TickCount64 + 2000;
        wasRunning = running;
        if (!running && Environment.TickCount64 >= releaseUntil) { frame = Empty; return; }
        var alive = new HashSet<LookAtActor>();
        bool playerDefeated = engine.IsActive && !engine.State.PlayerState.IsAlive;
        var actors = new Dictionary<nint, LookAtActor>();
        foreach (var obj in Core.Services.ObjectTable)
            if (obj.Address != 0) actors[obj.Address] = new(obj.Address, obj.EntityId);
        foreach (var npc in spawner.SpawnedNpcs)
            if (npc.IsSpawned && actors.TryGetValue(npc.Address, out var actor) && actor.EntityId == npc.SimulatedEntityId &&
                HeadLookAtTargetPolicy.CanDrive(npc.IsAlive, excluded(npc.Address), playerDefeated)) alive.Add(actor);
        if (!running) { frame = new(Array.Empty<Pair>(), alive); return; }
        var next = new List<Pair>();
        var player = Core.Services.ObjectTable.LocalPlayer;
        var playerActor = player == null ? default : new LookAtActor(player.Address, player.EntityId);
        foreach (var npc in spawner.SpawnedNpcs)
        {
            if (!actors.TryGetValue(npc.Address, out var enemy) || !alive.Contains(enemy)) continue;
            var combatTarget = default(LookAtActor);
            if (!playerDefeated)
            {
                var target = engine.GetNpcTarget(npc);
                if (target.IsPlayer) combatTarget = playerActor;
                else
                    foreach (var actor in actors.Values)
                        if (actor.EntityId == target.EntityId) { combatTarget = actor; break; }
            }
            if (HeadLookAtTargetPolicy.Select(enemy, combatTarget, playerActor, playerDefeated) is { } selected)
                next.Add(new Pair(enemy, selected));
        }
        frame = new(next.ToArray(), alive);
    }

    private void OnRenderFrame()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastRender == 0 ? 1f / 60 : Math.Clamp((float)Stopwatch.GetElapsedTime(lastRender, now).TotalSeconds, 0, .1f);
        lastRender = now;
        var snapshot = frame;
        if (swings.Count == 0 && snapshot.Pairs.Length == 0) return;
        var boneName = string.IsNullOrWhiteSpace(config.VirtualEnemyLookAtBone) ? "j_kao" : config.VirtualEnemyLookAtBone.Trim();
        var current = snapshot.Pairs;

        foreach (var pair in current)
        {
            if (!IsCurrentActor(pair.Enemy) || !IsCurrentActor(pair.Target)) { swings.Remove(pair.Enemy); continue; }
            var target = TargetPoint(pair.Target.Address);
            Apply(pair.Enemy, boneName, target, dt);
        }
        // Enemies no longer looking (disabled, died, lost target) ease back to their animation.
        released.Clear();
        foreach (var enemy in swings.Keys)
        {
            bool active = false;
            foreach (var pair in current) if (pair.Enemy == enemy) { active = true; break; }
            if (!active) released.Add(enemy);
        }
        var stillThere = snapshot.Living;
        foreach (var enemy in released)
        {
            // Despawned or dead: never touch its memory again; its ragdoll owns the head now.
            if (stillThere.Contains(enemy) && IsCurrentActor(enemy)) Apply(enemy, boneName, null, dt);
            else swings.Remove(enemy);
        }
    }

    private Vector3? TargetPoint(nint target)
    {
        var head = bones.GetBoneWorldPos(target, "j_kao");
        if (head.HasValue && Finite(head.Value)) return head;
        var obj = (GameObject*)target;
        var feet = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        return Finite(feet) ? feet + new Vector3(0, 1.5f, 0) : null;
    }

    private static bool IsCurrentActor(LookAtActor actor)
    {
        foreach (var obj in Core.Services.ObjectTable)
            if (obj.Address == actor.Address && obj.EntityId == actor.EntityId) return true;
        return false;
    }

    public IReadOnlyList<string> GetAvailableBoneNames()
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var npc in spawner.SpawnedNpcs)
        {
            if (!npc.IsSpawned) continue;
            LookAtActor identity = default;
            foreach (var obj in Core.Services.ObjectTable)
                if (obj.Address == npc.Address) { identity = new(obj.Address, obj.EntityId); break; }
            if (!identity.Valid) continue;
            foreach (var name in bones.GetBoneNames(identity.Address)) names.Add(name);
        }
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null)
            foreach (var name in bones.GetBoneNames(player.Address)) names.Add(name);
        return new List<string>(names);
    }

    private void Apply(LookAtActor enemy, string boneName, Vector3? targetWorld, float dt)
    {
        var maybe = bones.TryGetSkeleton(enemy.Address);
        if (maybe == null) { swings.Remove(enemy); return; }
        var skel = maybe.Value;
        var root = skel.CharBase->Skeleton;
        int bone = bones.ResolveBoneIndex(skel, boneName);
        if (root == null || bone < 0 || bone >= skel.BoneCount) { swings.Remove(enemy); return; }

        ref var pose = ref skel.Pose->ModelPose.Data[bone];
        var rotation = Quaternion.Normalize(new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W));
        var desired = Quaternion.Identity;
        if (targetWorld.HasValue)
        {
            var t = root->Transform;
            var skelRotation = Quaternion.Normalize(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W));
            var scale = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
            if (!(scale.X > 1e-4f && scale.Y > 1e-4f && scale.Z > 1e-4f)) scale = Vector3.One;
            var targetModel = Vector3.Transform(targetWorld.Value - new Vector3(t.Position.X, t.Position.Y, t.Position.Z),
                Quaternion.Inverse(skelRotation)) / scale;
            desired = HeadLookAtMath.Swing(rotation, BindModelRotation(skel, bone),
                new Vector3(pose.Translation.X, pose.Translation.Y, pose.Translation.Z), targetModel, MaxYaw, MaxPitch);
        }

        var swing = swings.TryGetValue(enemy, out var previous) ? previous : Quaternion.Identity;
        float rate = targetWorld.HasValue ? TurnRate : ReleaseRate;
        swing = Quaternion.Normalize(Quaternion.Slerp(swing, desired, 1 - MathF.Exp(-rate * dt)));
        if (!targetWorld.HasValue && MathF.Abs(swing.W) > .99999f) { swings.Remove(enemy); return; }
        swings[enemy] = swing;

        delta.Clear();
        delta[bone] = HeadLookAtMath.LocalDelta(rotation, swing);
        var result = bones.ApplyRotationDeltas(skel, delta);
        // The face and hair are separate partial skeletons rooted at the head.
        int head = bones.ResolveBoneIndex(skel, "j_kao");
        if (head >= 0 && head < result.HasAccumulated.Length && result.HasAccumulated[head])
            bones.PropagateToPartialSkeletons(skel, head, "j_kao", result);
    }

    /// <summary>The bone's model-space rotation in the bind pose (facing +Z).</summary>
    private static Quaternion BindModelRotation(SkeletonAccess skel, int bone)
    {
        var havok = skel.HavokSkeleton;
        var rotation = Quaternion.Identity;
        for (int b = bone, guard = 0; b >= 0 && b < havok->ReferencePose.Length && guard < 512; guard++)
        {
            var local = havok->ReferencePose.Data[b].Rotation;
            rotation = Quaternion.Normalize(new Quaternion(local.X, local.Y, local.Z, local.W) * rotation);
            b = b < havok->ParentIndices.Length ? havok->ParentIndices[b] : -1;
        }
        return rotation;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public void Dispose()
    {
        Core.Services.Framework.Update -= OnFrameworkUpdate;
        bones.OnFinalPoseModifiers -= OnRenderFrame;
        frame = Empty; swings.Clear();
    }
}
