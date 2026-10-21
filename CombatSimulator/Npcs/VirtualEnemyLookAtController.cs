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

    private readonly record struct Pair(nint Enemy, nint Target);
    // Built on the framework thread (simulation state lives there); read by the render hook.
    private volatile Pair[] pairs = Array.Empty<Pair>();
    // Enemies that still exist and are alive; only these may be touched when easing back.
    private volatile HashSet<nint> living = new();
    private readonly Dictionary<int, Quaternion> delta = new(1);
    private readonly Dictionary<nint, Quaternion> swings = new();
    private readonly List<nint> released = new();
    private long lastRender;

    public VirtualEnemyLookAtController(BoneTransformService bones, Configuration config, NpcSpawner spawner,
        CombatEngine engine, Func<nint, bool> excluded, IPluginLog log)
    {
        this.bones = bones; this.config = config; this.spawner = spawner;
        this.engine = engine; this.excluded = excluded; this.log = log;
        Core.Services.Framework.Update += OnFrameworkUpdate;
        bones.OnRenderFrame += OnRenderFrame;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var alive = new HashSet<nint>();
        foreach (var npc in spawner.SpawnedNpcs)
            if (npc.IsSpawned && npc.BattleChara != null && npc.IsAlive && !excluded(npc.Address)) alive.Add(npc.Address);
        living = alive;
        if (!config.VirtualEnemyLookAtTarget || !engine.IsActive) { pairs = Array.Empty<Pair>(); return; }
        var next = new List<Pair>();
        var player = Core.Services.ObjectTable.LocalPlayer;
        foreach (var npc in spawner.SpawnedNpcs)
        {
            if (!alive.Contains(npc.Address)) continue;
            var target = engine.GetNpcTarget(npc);
            if (!target.IsAlive) continue;
            nint address = 0;
            if (target.IsPlayer) address = player?.Address ?? 0;
            else
                foreach (var obj in Core.Services.ObjectTable)
                    if (obj.EntityId == target.EntityId) { address = obj.Address; break; }
            if (address != 0 && address != npc.Address) next.Add(new Pair(npc.Address, address));
        }
        pairs = next.ToArray();
    }

    private void OnRenderFrame()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastRender == 0 ? 1f / 60 : Math.Clamp((float)Stopwatch.GetElapsedTime(lastRender, now).TotalSeconds, 0, .1f);
        lastRender = now;
        if (swings.Count == 0 && pairs.Length == 0) return;
        var boneName = string.IsNullOrWhiteSpace(config.VirtualEnemyLookAtBone) ? "j_kao" : config.VirtualEnemyLookAtBone.Trim();
        var current = pairs;

        foreach (var pair in current)
        {
            var target = TargetPoint(pair.Target);
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
        var stillThere = living;
        foreach (var enemy in released)
        {
            // Despawned or dead: never touch its memory again; its ragdoll owns the head now.
            if (stillThere.Contains(enemy)) Apply(enemy, boneName, null, dt);
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

    private void Apply(nint enemy, string boneName, Vector3? targetWorld, float dt)
    {
        var maybe = bones.TryGetSkeleton(enemy);
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
        bones.OnRenderFrame -= OnRenderFrame;
        pairs = Array.Empty<Pair>(); swings.Clear();
    }
}
