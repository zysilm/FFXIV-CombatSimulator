// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

/// <summary>
/// Keeps an actor that is flown down into the ground on top of it: its feet stay on the ground and
/// its legs fold, and pushed further it goes down on its front (see <see cref="GroundConformSolver"/>).
/// Whoever moves the actor says every tick how far below the ground its root now is; the pose is
/// refitted each rendered frame, after the animation and before the actor is drawn.
///
/// A skeleton is tried out on the pose it has when it first goes under, by pushing it all the way
/// down, and on a few later poses if that one fails: if the fit does not come through cleanly the
/// actor is left to sink into the ground as it always did.
/// </summary>
public sealed unsafe class GroundConformPoser : IDisposable
{
    private const int StaleAfterMissedFrames = 3;
    // How fast the ground under an actor may seem to move, metres a second: a floor found a step
    // higher or lower from one tick to the next eases in instead of snapping the pose.
    private const float DepthSpeed = 6f;
    // How often, and how many frames apart, a skeleton is tried out before it is left to sink.
    private const int MostChecks = 6;
    private const int ChecksApart = 7;

    private sealed class ActorState
    {
        public float Depth;            // metres the root is below the ground, as asked for
        public float Shown;            // and as shown
        public float DeltaTime = 1f / 60f;
        public int MissedFrames;
        public nint Skeleton;          // the skeleton the check below was made for
        public bool Handled;
        public int Checks;             // how many times the check was made for it
        public int SinceCheck;         // frames since the last
        public float Height;           // of the pose, metres
        public float Deepest = float.NaN; // how far under the ground the body is as low as it goes, metres
        public readonly GroundConformSolver Solver = new();
        public int[] Parents = Array.Empty<int>();
        public Vector3[] Positions = Array.Empty<Vector3>();
        public Vector3[] Before = Array.Empty<Vector3>();
        public Quaternion[] Rotations = Array.Empty<Quaternion>();
    }

    private readonly BoneTransformService boneService;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, ActorState> actors = new();
    private readonly List<nint> stale = new();
    private readonly HashSet<string> partialRoots = new(StringComparer.Ordinal);

    public GroundConformPoser(BoneTransformService boneService, IPluginLog log)
    {
        this.boneService = boneService;
        this.log = log;
        boneService.OnRenderFrame += OnRenderFrame;
    }

    /// <summary>
    /// Say, every tick the actor is held below the ground, how far below it its root is (metres; 0
    /// or less when it is on or above the ground). Actors no longer reported are forgotten.
    /// </summary>
    public void Track(nint actorAddress, float depthBelowGround, float deltaTime)
    {
        if (actorAddress == nint.Zero || !float.IsFinite(depthBelowGround)) return;
        if (!actors.TryGetValue(actorAddress, out var state))
        {
            if (depthBelowGround <= 0f) return;
            actors[actorAddress] = state = new ActorState();
        }
        state.Depth = MathF.Max(0f, depthBelowGround);
        state.DeltaTime = deltaTime > 0f ? deltaTime : state.DeltaTime;
        state.MissedFrames = 0;
    }

    /// <summary>
    /// How deep it is worth pushing this actor: to where it is as low over the ground as it goes,
    /// or a little more than its own height for a body that only folds its legs. Null until the
    /// actor has been under the ground once.
    /// </summary>
    public float? DeepestUseful(nint actorAddress)
    {
        if (!actors.TryGetValue(actorAddress, out var state) || state.Height <= 0f) return null;
        return float.IsFinite(state.Deepest) ? MathF.Min(state.Deepest, state.Height * 2f) : state.Height * 1.2f;
    }

    private void OnRenderFrame()
    {
        if (actors.Count == 0) return;
        foreach (var (address, state) in actors)
        {
            if (++state.MissedFrames > StaleAfterMissedFrames)
            {
                stale.Add(address);
                continue;
            }
            try
            {
                Pose(address, state);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"GroundConform: fit failed for 0x{address:X}; dropping");
                stale.Add(address);
            }
        }
        foreach (var address in stale) actors.Remove(address);
        stale.Clear();
    }

    private void Pose(nint address, ActorState state)
    {
        var step = DepthSpeed * state.DeltaTime;
        state.Shown = MathF.Abs(state.Depth - state.Shown) <= step ? state.Depth : state.Shown + MathF.Sign(state.Depth - state.Shown) * step;
        if (state.Shown <= 0f) return;

        var skelN = boneService.TryGetSkeleton(address);
        if (skelN == null) return;
        var skel = skelN.Value;
        var count = Math.Min(skel.BoneCount, skel.ParentCount);
        if (count < 2 || skel.Pose == null || skel.HavokSkeleton == null) return;

        var gameObject = (GameObject*)address;
        var scale = gameObject->DrawObject != null ? gameObject->DrawObject->Scale.Y : 1f;
        if (!float.IsFinite(scale) || scale <= 1e-4f) scale = 1f;

        if (state.Positions.Length != count)
        {
            state.Positions = new Vector3[count];
            state.Before = new Vector3[count];
            state.Rotations = new Quaternion[count];
            state.Parents = new int[count];
            state.Skeleton = nint.Zero;
        }
        for (var i = 0; i < count; i++)
        {
            ref var model = ref skel.Pose->ModelPose.Data[i];
            state.Positions[i] = new Vector3(model.Translation.X, model.Translation.Y, model.Translation.Z);
            state.Rotations[i] = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
            state.Parents[i] = i > 0 ? skel.HavokSkeleton->ParentIndices[i] : -1;
        }

        // The skeleton's own space: Y up, and the actor faces along +Z.
        var forward = Vector3.UnitZ;
        if (state.Skeleton != (nint)skel.HavokSkeleton)
        {
            state.Skeleton = (nint)skel.HavokSkeleton;
            state.Handled = false;
            state.Checks = 0;
            state.SinceCheck = ChecksApart;
            float lowest = float.MaxValue, highest = float.MinValue;
            foreach (var p in state.Positions)
            {
                lowest = MathF.Min(lowest, p.Y);
                highest = MathF.Max(highest, p.Y);
            }
            state.Height = MathF.Max(0.1f, (highest - lowest) * scale);
        }
        // The check is made on the pose the actor happens to be in, and a pose can be a poor one to
        // judge by (a foot lifted high in a running step): one that fails is tried again on a few
        // later poses before the actor is given up.
        if (!state.Handled && state.Checks < MostChecks && ++state.SinceCheck > ChecksApart)
        {
            state.SinceCheck = 0;
            state.Checks++;
            state.Handled = GroundConformSolver.Handles(state.Parents, state.Positions, state.Rotations, forward);
            if (state.Handled || state.Checks == MostChecks)
                log.Info($"GroundConform: 0x{address:X} ({count} bones, {state.Height:F2} m) {(state.Handled ? "is fitted to the ground" : "is left to sink: the fit did not pass its own check on this skeleton")}");
        }
        if (!state.Handled) return;

        Array.Copy(state.Positions, state.Before, count);
        if (!state.Solver.Solve(state.Parents, state.Positions, state.Rotations, state.Shown / scale, forward))
            return;
        state.Deepest = state.Solver.LastDeepest * scale;

        var result = new BoneModificationResult(count);
        for (var i = 0; i < count; i++)
        {
            ref var model = ref skel.Pose->ModelPose.Data[i];
            result.OriginalPositions[i] = state.Before[i];
            result.OriginalRotations[i] = new Quaternion(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
        }
        for (var i = 0; i < count; i++)
        {
            if (!float.IsFinite(state.Positions[i].X + state.Positions[i].Y + state.Positions[i].Z) ||
                !float.IsFinite(state.Rotations[i].X + state.Rotations[i].Y + state.Rotations[i].Z + state.Rotations[i].W))
                continue;
            if (Vector3.DistanceSquared(state.Positions[i], state.Before[i]) < 1e-12f &&
                Quaternion.Dot(state.Rotations[i], result.OriginalRotations[i]) > 0.9999999f)
                continue;
            boneService.WriteBoneTransform(skel, i, state.Positions[i], state.Rotations[i], result);
        }

        // Faces, hair and the like hang from bones of this skeleton as skeletons of their own.
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return;
        partialRoots.Clear();
        for (var partial = 1; partial < skeleton->PartialSkeletonCount; partial++)
        {
            var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Length < 1) continue;
            var name = pose->Skeleton->Bones[0].Name.String;
            if (string.IsNullOrEmpty(name) || !partialRoots.Add(name)) continue;
            var bone = boneService.ResolveBoneIndex(skel, name);
            if (bone >= 0 && bone < count && result.HasAccumulated[bone])
                boneService.PropagateToPartialSkeletons(skel, bone, name, result);
        }
    }

    public void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        actors.Clear();
    }
}
