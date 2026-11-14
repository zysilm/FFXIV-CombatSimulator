// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Animation.Recording;
using CombatSimulator.Animation.SkeletonProfiles;
using System.Collections.Immutable;
using BepuPhysics;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    private sealed class NpcTraversalAnchor
    {
        public BodyHandle NpcBody;
        public float NpcAlongAxis;
        public BodyHandle CorpseBody;
        // The support sample is retained in the corpse body's local frame.  This lets the NPC follow
        // the same physical patch when the body translates or rotates instead of alternating between
        // a stale world-space height and a newly acquired surface on consecutive update/render frames.
        public Vector3 CorpseLocalSupportCenter;
        public float RootToSupportCenterY;
        public float SupportRootY;
        public long LastConfirmedTimestamp;
    }

    private const float NpcTraversalAnchorGraceSeconds = 0.12f;
    private const float NpcTraversalAnchorCorrectionDeadband = 0.012f;
    private const float NpcTraversalAnchorCorrectionRateDown = 0.28f;
    private const float NpcTraversalAnchorCorrectionRateUp = 0.55f;

    private readonly struct NpcTraversalCapsule
    {
        public readonly BodyHandle Handle;
        public readonly Vector3 Center;
        public readonly Quaternion Orientation;
        public readonly float Radius;
        public readonly float HalfLength;
        public readonly float Bottom;
        public readonly float Top;

        public NpcTraversalCapsule(
            BodyHandle handle,
            Vector3 center,
            Quaternion orientation,
            float radius,
            float halfLength,
            float bottom,
            float top)
        {
            Handle = handle;
            Center = center;
            Orientation = orientation;
            Radius = radius;
            HalfLength = halfLength;
            Bottom = bottom;
            Top = top;
        }
    }

    private readonly struct NpcTraversalSurfaceHit
    {
        public readonly float RequiredCenterY;
        public readonly BodyHandle CorpseBody;

        public NpcTraversalSurfaceHit(float requiredCenterY, BodyHandle corpseBody)
        {
            RequiredCenterY = requiredCenterY;
            CorpseBody = corpseBody;
        }
    }

    /// <summary>A soft navigation reference sampled from one structural corpse collider. The ID is
    /// stable for the lifetime of the current physics simulation; no constraint owns this point.</summary>
    public readonly struct CorpseTraversalSlot
    {
        public readonly int Id;
        public readonly Vector3 Position;
        public readonly float Clearance;
        public readonly float SelectionWeight;
        public readonly float WanderRadius;
        public readonly bool IsFace;
        public readonly string Label;
        // Optional structural body that should provide vertical support for this landmark. Face
        // landmarks use j_kao so a reacquisition cannot jump between head, neck and shoulder.
        public readonly int SupportBodyHandle;

        public CorpseTraversalSlot(
            int id,
            Vector3 position,
            float clearance,
            float selectionWeight,
            float wanderRadius,
            bool isFace,
            string label,
            int supportBodyHandle = -1)
        {
            Id = id;
            Position = position;
            Clearance = clearance;
            SelectionWeight = selectionWeight;
            WanderRadius = wanderRadius;
            IsFace = isFace;
            Label = label;
            SupportBodyHandle = supportBodyHandle;
        }
    }

    /// <summary>
    /// Temporarily scale dynamic ragdoll mass/inertia for kinematic traversal contacts. This is not
    /// a gravity or animation-speed trick: inverse mass and the complete inverse inertia tensor are
    /// scaled together, so footsteps impart less linear and angular acceleration. Rigid-grab restore
    /// inertias are updated too, preventing a grabbed bone from reappearing with a stale mass.
    /// </summary>
    private void SetTraversalMassMultiplierCore(float multiplier)
    {
        multiplier = float.IsFinite(multiplier) ? Math.Clamp(multiplier, 1f, 12f) : 1f;
        if (MathF.Abs(multiplier - traversalMassMultiplier) < 0.001f)
            return;

        if (simulation == null || !isActive || !physicsStarted)
        {
            traversalMassMultiplier = 1f;
            return;
        }

        var inverseScale = traversalMassMultiplier / multiplier;
        foreach (var bone in ragdollBones)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(bone.BodyHandle);
                var inertia = body.LocalInertia;
                if (inertia.InverseMass <= 0f)
                    continue;
                ScaleInverseInertia(ref inertia, inverseScale);
                body.SetLocalInertia(inertia);
                body.Awake = true;
            }
            catch { }
        }


        traversalMassMultiplier = multiplier;
        prevAllAsleep = false;
        BeginBiomechanicalSettle(0.25f);
        log.Info($"RagdollController: traversal mass {multiplier:F1}x for 0x{targetCharacterAddress:X}.");
    }

    private static void ScaleInverseInertia(ref BodyInertia inertia, float scale)
    {
        inertia.InverseMass *= scale;
        inertia.InverseInertiaTensor.XX *= scale;
        inertia.InverseInertiaTensor.YX *= scale;
        inertia.InverseInertiaTensor.YY *= scale;
        inertia.InverseInertiaTensor.ZX *= scale;
        inertia.InverseInertiaTensor.ZY *= scale;
        inertia.InverseInertiaTensor.ZZ *= scale;
    }

    /// <summary>Forget the retained corpse-support pair for one live NPC. Call when an external
    /// movement controller stops ground traversal or releases ownership of that actor.</summary>
    private void ClearNpcTraversalSupportCore(nint npcAddress)
    {
        if (npcAddress != nint.Zero)
            npcTraversalAnchors.Remove(npcAddress);
    }

    private bool TryCollectNpcTraversalCapsules(
        in NpcCollisionState npcState,
        bool lightweightAttackMode,
        List<NpcTraversalCapsule> destination)
    {
        destination.Clear();
        if (simulation == null)
            return false;

        try
        {
            if (npcState.IsFallback)
            {
                var body = simulation.Bodies.GetBodyReference(npcState.FallbackHandle);
                var scale = MathF.Max(0.0001f, npcState.AppliedCollisionScale);
                var radius = npcState.FallbackBaseRadius * scale;
                var halfLength = npcState.FallbackBaseLength * scale * 0.5f;
                var extentY = halfLength + radius;
                destination.Add(new NpcTraversalCapsule(
                    npcState.FallbackHandle,
                    body.Pose.Position,
                    body.Pose.Orientation,
                    radius,
                    halfLength,
                    body.Pose.Position.Y - extentY,
                    body.Pose.Position.Y + extentY));
            }
            else if (!npcState.IsMesh)
            {
                for (var boneIndex = 0; boneIndex < npcState.BoneStatics.Count; boneIndex++)
                {
                    if (!IsNpcBoneProxyActive(npcState, boneIndex, lightweightAttackMode))
                        continue;
                    var bone = npcState.BoneStatics[boneIndex];
                    var body = simulation.Bodies.GetBodyReference(bone.Handle);
                    var logicalCenter = body.Pose.Position -
                                        Vector3.Transform(bone.ShapeCenterOffset, body.Pose.Orientation);
                    var scale = MathF.Max(0.0001f, bone.AppliedCollisionScale);
                    var radius = bone.BaseRadius * scale;
                    var halfLength = bone.BaseHalfLength * scale;
                    var axis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
                    var extentY = MathF.Abs(axis.Y) * halfLength + radius;
                    destination.Add(new NpcTraversalCapsule(
                        bone.Handle,
                        logicalCenter,
                        body.Pose.Orientation,
                        radius,
                        halfLength,
                        logicalCenter.Y - extentY,
                        logicalCenter.Y + extentY));
                }
            }
        }
        catch
        {
            destination.Clear();
        }

        return destination.Count > 0;
    }

    private bool TryResolveTrackedNpcSupport(
        NpcTraversalAnchor anchor,
        float terrainY,
        out float rootY,
        out Vector3 supportCenter)
    {
        rootY = terrainY;
        supportCenter = default;
        if (simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(anchor.CorpseBody);
            supportCenter = body.Pose.Position +
                            Vector3.Transform(anchor.CorpseLocalSupportCenter, body.Pose.Orientation);
            rootY = MathF.Max(terrainY, supportCenter.Y + anchor.RootToSupportCenterY);
            return float.IsFinite(rootY);
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateNpcTraversalAnchor(
        NpcTraversalAnchor anchor,
        Vector3 supportCenter,
        float supportRootY,
        long confirmedTimestamp)
    {
        if (simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(anchor.CorpseBody);
            var inverse = Quaternion.Inverse(body.Pose.Orientation);
            anchor.CorpseLocalSupportCenter = Vector3.Transform(
                supportCenter - body.Pose.Position, inverse);
            anchor.RootToSupportCenterY = supportRootY - supportCenter.Y;
            anchor.SupportRootY = supportRootY;
            anchor.LastConfirmedTimestamp = confirmedTimestamp;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the root height required for the registered NPC's real lowest BEPU capsules to
    /// contact the visible ragdoll volumes. Unlike the general foot probe, this does not invent a
    /// navigation footprint: every query originates from an NPC capsule the simulation is actually
    /// using, and every target is one of the corpse shapes drawn by the collision debug overlay.
    /// </summary>
    private bool TryGetNpcTraversalRootHeightCore(
        nint npcAddress,
        Vector3 proposedRootPosition,
        float terrainY,
        float maxClimb,
        out float rootY,
        int preferredCorpseBodyHandle = -1)
    {
        rootY = terrainY;
        if (simulation == null || !isActive || !physicsStarted || npcAddress == nint.Zero)
            return false;

        NpcCollisionState? matchedState = null;
        foreach (var state in npcCollisionStates)
        {
            if (state.NpcAddress == npcAddress && !state.Parked)
            {
                matchedState = state;
                break;
            }
        }
        if (!matchedState.HasValue)
            return false;

        var npcState = matchedState.Value;
        var capsules = npcTraversalCapsuleBuffer;
        if (!TryCollectNpcTraversalCapsules(npcState, npcState.LightweightAttackMode, capsules))
            return false;

        var minBottom = float.MaxValue;
        var maxTop = float.MinValue;
        foreach (var capsule in capsules)
        {
            minBottom = MathF.Min(minBottom, capsule.Bottom);
            maxTop = MathF.Max(maxTop, capsule.Top);
        }
        var bodyHeight = MathF.Max(0.0001f, maxTop - minBottom);
        // Only the lowest visible collision layer can acquire support. Once acquired, the exact
        // capsule/body pair is retained below instead of competing with every foot every frame.
        var lowerBandTop = minBottom + MathF.Max(0.0002f, bodyHeight * 0.18f);

        var currentRoot = actors.GetPosition(npcAddress, ActorReadSite.TraversalRoot);
        if (rec != null) RecActorPosition(ActorSite.TraversalRoot, npcAddress, currentRoot);
        var proxySampleRoot = npcState.HasTraversalSampleRoot
            ? npcState.TraversalSampleRoot
            : currentRoot;
        var proposedRootDelta = proposedRootPosition - proxySampleRoot;
        BodyHandle? preferredAcquisitionBody = preferredCorpseBodyHandle >= 0
            ? new BodyHandle(preferredCorpseBodyHandle)
            : null;
        maxClimb = MathF.Max(0f, maxClimb);

        bool TryEvaluateAcquisition(
            NpcTraversalCapsule capsule,
            float along,
            Vector3 sampledRootPosition,
            bool horizontalHandoff,
            out float candidateRootY,
            out NpcTraversalSurfaceHit hit,
            out Vector3 supportCenter)
        {
            candidateRootY = terrainY;
            hit = default;
            supportCenter = default;
            var axis = Vector3.Transform(Vector3.UnitY, capsule.Orientation);
            var sphereCenter = capsule.Center + axis * along + (sampledRootPosition - proxySampleRoot);
            if (!TryGetSphereSupportHit(
                    sphereCenter, capsule.Radius, horizontalHandoff,
                    preferredAcquisitionBody, out hit))
                return false;

            var verticalCorrection = hit.RequiredCenterY - sphereCenter.Y;
            var penetration = Math.Clamp(capsule.Radius * 0.12f, 0.0001f, 0.01f);
            candidateRootY = sampledRootPosition.Y + verticalCorrection - penetration;

            if (!horizontalHandoff)
            {
                if (hit.RequiredCenterY - capsule.Radius <= terrainY + 0.001f)
                    return false;
                var allowedGap = MathF.Max(0.002f, capsule.Radius * 0.5f);
                if (verticalCorrection < -allowedGap || candidateRootY > terrainY + maxClimb)
                    return false;
            }

            candidateRootY = MathF.Max(terrainY, candidateRootY);
            supportCenter = new Vector3(sphereCenter.X, hit.RequiredCenterY, sphereCenter.Z);
            return horizontalHandoff || candidateRootY > terrainY + 0.001f;
        }

        var queryTimestamp = clock.GetTimestamp(ClockSite.NpcTraversalQuery);
        float? previousSupportRootY = null;
        if (npcTraversalAnchors.TryGetValue(npcAddress, out var anchor))
        {
            foreach (var capsule in capsules)
            {
                if (capsule.Handle != anchor.NpcBody)
                    continue;

                // Maintenance is horizontal-only. The corpse is dynamic and is expected to move
                // down when loaded; using that vertical response as the next NPC root target is a
                // positive feedback loop. The exact NPC sample/corpse pair remains support while
                // their XZ volumes overlap with an upward-facing contact region.
                var axis = Vector3.Transform(Vector3.UnitY, capsule.Orientation);
                var sphereCenter = capsule.Center + axis * anchor.NpcAlongAxis + proposedRootDelta;
                if (TryGetSphereSupportHit(
                        sphereCenter, capsule.Radius, true, anchor.CorpseBody, out var retainedHit))
                {
                    var penetration = Math.Clamp(capsule.Radius * 0.12f, 0.0001f, 0.01f);
                    var rawRootY = MathF.Max(terrainY,
                        proposedRootPosition.Y + retainedHit.RequiredCenterY - sphereCenter.Y - penetration);
                    var retainedSupportCenter = new Vector3(
                        sphereCenter.X, retainedHit.RequiredCenterY, sphereCenter.Z);

                    // Follow the same local patch of the moving corpse, then admit only a small
                    // correction toward the freshly measured surface. This removes the one-frame
                    // render/update phase jump without recreating the old corpse-down/NPC-down
                    // positive feedback loop.
                    var trackedRootY = anchor.SupportRootY;
                    if (TryResolveTrackedNpcSupport(anchor, terrainY, out var resolvedRootY, out _))
                        trackedRootY = resolvedRootY;
                    var correctionElapsed = anchor.LastConfirmedTimestamp > 0
                        ? Math.Clamp(
                            (queryTimestamp - anchor.LastConfirmedTimestamp) /
                            (double)clock.Frequency,
                            0.0,
                            0.05)
                        : 1.0 / 60.0;
                    var correctionError = rawRootY - trackedRootY;
                    var correction = MathF.Abs(correctionError) <= NpcTraversalAnchorCorrectionDeadband
                        ? 0f
                        : Math.Clamp(
                            correctionError,
                            -NpcTraversalAnchorCorrectionRateDown * (float)correctionElapsed,
                            NpcTraversalAnchorCorrectionRateUp * (float)correctionElapsed);
                    rootY = MathF.Max(terrainY, trackedRootY + correction);
                    if (!TryUpdateNpcTraversalAnchor(
                            anchor, retainedSupportCenter, rootY, queryTimestamp))
                        npcTraversalAnchors.Remove(npcAddress);
                    return true;
                }

                // A moving/rotating ragdoll can make the geometric query miss for one update even
                // though the foot is still over the same patch. Keep the local-frame support for a
                // short grace period, but only while the foot remains close in X/Z; genuine walk-off
                // still releases promptly.
                var graceElapsed = anchor.LastConfirmedTimestamp > 0
                    ? (queryTimestamp - anchor.LastConfirmedTimestamp) /
                      (double)clock.Frequency
                    : double.MaxValue;
                if (graceElapsed <= NpcTraversalAnchorGraceSeconds &&
                    TryResolveTrackedNpcSupport(
                        anchor, terrainY, out var graceRootY, out var trackedSupportCenter))
                {
                    var horizontalGap = Vector2.Distance(
                        new Vector2(sphereCenter.X, sphereCenter.Z),
                        new Vector2(trackedSupportCenter.X, trackedSupportCenter.Z));
                    var graceRadius = MathF.Max(0.08f, capsule.Radius * 2.5f);
                    if (horizontalGap <= graceRadius)
                    {
                        rootY = graceRootY;
                        return true;
                    }
                }
                break;
            }

            previousSupportRootY = anchor.SupportRootY;
            npcTraversalAnchors.Remove(npcAddress);
        }

        bool TryFindSupportAt(
            Vector3 sampledRootPosition,
            bool horizontalHandoff,
            float? referenceRootY,
            out float candidateRootY,
            out BodyHandle npcBody,
            out float npcAlongAxis,
            out BodyHandle corpseBody,
            out Vector3 supportCenter)
        {
            var found = false;
            candidateRootY = float.MinValue;
            npcBody = default;
            npcAlongAxis = 0f;
            corpseBody = default;
            supportCenter = default;
            foreach (var capsule in capsules)
            {
                if (capsule.Bottom > lowerBandTop)
                    continue;

                for (var sampleIndex = 0; sampleIndex < 3; sampleIndex++)
                {
                    var along = sampleIndex switch
                    {
                        0 => -capsule.HalfLength,
                        2 => capsule.HalfLength,
                        _ => 0f,
                    };
                    if (!TryEvaluateAcquisition(
                            capsule, along, sampledRootPosition, horizontalHandoff,
                            out var nextRootY, out var hit, out var nextSupportCenter))
                        continue;
                    var preferCandidate = !found || (referenceRootY.HasValue
                        ? MathF.Abs(nextRootY - referenceRootY.Value) <
                          MathF.Abs(candidateRootY - referenceRootY.Value)
                        : nextRootY > candidateRootY);
                    if (!preferCandidate)
                        continue;

                    found = true;
                    candidateRootY = nextRootY;
                    npcBody = capsule.Handle;
                    npcAlongAxis = along;
                    corpseBody = hit.CorpseBody;
                    supportCenter = nextSupportCenter;
                }
            }
            return found;
        }

        // Stable support is decided only at the requested destination. A crossed surface from an
        // earlier point in the frame must not become an anchor at the actor's final XZ position.
        if (TryFindSupportAt(
                proposedRootPosition, previousSupportRootY.HasValue, previousSupportRootY,
                out var bestRootY, out var bestNpcBody, out var bestAlongAxis,
                out var bestCorpseBody, out var bestSupportCenter))
        {
            if (previousSupportRootY.HasValue)
                bestRootY = MathF.Max(previousSupportRootY.Value, bestRootY);
            var newAnchor = new NpcTraversalAnchor
            {
                NpcBody = bestNpcBody,
                NpcAlongAxis = bestAlongAxis,
                CorpseBody = bestCorpseBody,
                SupportRootY = bestRootY,
                LastConfirmedTimestamp = queryTimestamp,
            };
            if (TryUpdateNpcTraversalAnchor(
                    newAnchor, bestSupportCenter, bestRootY, queryTimestamp))
                npcTraversalAnchors[npcAddress] = newAnchor;
            rootY = bestRootY;
            return true;
        }

        // A lost anchor is allowed to fall. New acquisition gets one continuous sweep so a narrow
        // support cannot be skipped between frames. A swept-only hit is a transient step-up, not a
        // retained body pair; it only tells the movement controller to clear the crossed surface.
        if (previousSupportRootY.HasValue)
            return false;

        var horizontalDelta = new Vector2(
            proposedRootPosition.X - currentRoot.X,
            proposedRootPosition.Z - currentRoot.Z);
        var sweepDistance = horizontalDelta.Length();
        if (sweepDistance <= 0.000001f)
            return false;

        var sweepRadius = float.MaxValue;
        foreach (var capsule in capsules)
        {
            if (capsule.Bottom <= lowerBandTop)
                sweepRadius = MathF.Min(sweepRadius, capsule.Radius);
        }
        if (sweepRadius == float.MaxValue)
            return false;

        var sweepStep = MathF.Max(0.001f, sweepRadius * 0.5f);
        var sweepSamples = Math.Min(128, Math.Max(1,
            (int)MathF.Ceiling(sweepDistance / sweepStep)));
        for (var sweepIndex = 1; sweepIndex < sweepSamples; sweepIndex++)
        {
            var sampledRootPosition = Vector3.Lerp(
                currentRoot, proposedRootPosition, sweepIndex / (float)sweepSamples);
            if (!TryFindSupportAt(
                    sampledRootPosition, false, null,
                    out bestRootY, out _, out _, out _, out _))
                continue;
            rootY = bestRootY;
            return true;
        }

        return false;
    }

    /// <summary>Pure look-ahead version of corpse support acquisition. It samples the registered
    /// NPC's real lowest proxies at a future root position but never creates, replaces, or removes a
    /// retained support anchor. Swarm steering uses this to begin raising the root before its
    /// kinematic proxies deliver a side-on normal impulse to the corpse.</summary>
    public bool TryPreviewNpcTraversalRootHeight(
        nint npcAddress,
        Vector3 previewRootPosition,
        float terrainY,
        float maxClimb,
        out float rootY,
        Vector3? pathStartRootPosition = null,
        int pathSampleCount = 1,
        int preferredCorpseBodyHandle = -1)
    {
        rootY = terrainY;
        if (simulation == null || !isActive || !physicsStarted || npcAddress == nint.Zero)
            return false;

        NpcCollisionState? matchedState = null;
        foreach (var state in npcCollisionStates)
        {
            if (state.NpcAddress == npcAddress && !state.Parked)
            {
                matchedState = state;
                break;
            }
        }
        if (!matchedState.HasValue)
            return false;

        var npcState = matchedState.Value;
        var capsules = npcTraversalCapsuleBuffer;
        if (!TryCollectNpcTraversalCapsules(npcState, false, capsules))
            return false;

        var minBottom = float.MaxValue;
        var maxTop = float.MinValue;
        foreach (var capsule in capsules)
        {
            minBottom = MathF.Min(minBottom, capsule.Bottom);
            maxTop = MathF.Max(maxTop, capsule.Top);
        }
        var lowerBandTop = minBottom + MathF.Max(0.0002f, (maxTop - minBottom) * 0.18f);
        var currentRoot = actors.GetPosition(npcAddress, ActorReadSite.TraversalPreviewRoot);
        if (rec != null) RecActorPosition(ActorSite.TraversalPreviewRoot, npcAddress, currentRoot);
        var proxySampleRoot = npcState.HasTraversalSampleRoot
            ? npcState.TraversalSampleRoot
            : currentRoot;
        BodyHandle? preferredPreviewBody = preferredCorpseBodyHandle >= 0
            ? new BodyHandle(preferredCorpseBodyHandle)
            : null;
        var found = false;
        var bestRootY = float.MinValue;
        pathSampleCount = Math.Clamp(pathSampleCount, 1, 8);
        var pathStart = pathStartRootPosition ?? previewRootPosition;

        for (var pathSample = 1; pathSample <= pathSampleCount; pathSample++)
        {
            var sampledRootPosition = pathSampleCount == 1
                ? previewRootPosition
                : Vector3.Lerp(pathStart, previewRootPosition, pathSample / (float)pathSampleCount);
            var rootDelta = sampledRootPosition - proxySampleRoot;
            foreach (var capsule in capsules)
            {
                if (capsule.Bottom > lowerBandTop)
                    continue;
                var axis = Vector3.Transform(Vector3.UnitY, capsule.Orientation);
                for (var sampleIndex = 0; sampleIndex < 3; sampleIndex++)
                {
                    var along = sampleIndex switch
                    {
                        0 => -capsule.HalfLength,
                        2 => capsule.HalfLength,
                        _ => 0f,
                    };
                    var sphereCenter = capsule.Center + axis * along + rootDelta;
                    if (!TryGetSphereSupportHit(
                            sphereCenter, capsule.Radius, false, preferredPreviewBody, out var hit))
                        continue;
                    var verticalCorrection = hit.RequiredCenterY - sphereCenter.Y;
                    var penetration = Math.Clamp(capsule.Radius * 0.12f, 0.0001f, 0.01f);
                    var candidate = sampledRootPosition.Y + verticalCorrection - penetration;
                    var allowedGap = MathF.Max(0.002f, capsule.Radius * 0.5f);
                    if (hit.RequiredCenterY - capsule.Radius <= terrainY + 0.001f ||
                        verticalCorrection < -allowedGap || candidate > terrainY + MathF.Max(0f, maxClimb))
                        continue;
                    candidate = MathF.Max(terrainY, candidate);
                    if (candidate <= terrainY + 0.001f || (found && candidate <= bestRootY))
                        continue;
                    found = true;
                    bestRootY = candidate;
                }
            }
        }

        if (!found)
            return false;
        rootY = bestRootY;
        return true;
    }

    /// <summary>
    /// Vertical support for the center of a real NPC collider sphere against the exact corpse
    /// shapes. Capsule radii are combined as a true sphere/capsule pair. Boxes intentionally use
    /// their unexpanded visible top face; this is conservative at rounded edge contacts and avoids
    /// recreating the oversized invisible foot disk that caused hovering.
    /// </summary>
    private bool TryGetSphereSupportHit(
        Vector3 sphereCenter,
        float sphereRadius,
        bool maintainContact,
        BodyHandle? preferredCorpseBody,
        out NpcTraversalSurfaceHit hit)
    {
        hit = default;
        if (simulation == null || sphereRadius <= 0f)
            return false;

        // Profile mode is authoritative for this frame: do not combine it with the legacy
        // capsule/box estimator, because selecting the higher result from two representations is
        // exactly how invisible oversized support and hovering return.
        if (characterSurfaceRuntime != null)
        {
            var profileFound = false;
            var profileCenterY = float.MinValue;
            BodyHandle profileBody = default;
            foreach (var rb in ragdollBones)
            {
                if (preferredCorpseBody.HasValue && rb.BodyHandle.Value != preferredCorpseBody.Value.Value)
                    continue;
                if (!IsTraversalSurfaceBone(rb))
                    continue;
                if (!characterSurfaceRuntime.TryGetVerticalSupport(
                        sphereCenter, sphereRadius, rb.Name, out var candidate, out _))
                    continue;
                if (candidate <= profileCenterY)
                    continue;
                profileFound = true;
                profileCenterY = candidate;
                profileBody = rb.BodyHandle;
            }

            if (profileFound)
                hit = new NpcTraversalSurfaceHit(profileCenterY, profileBody);
            return profileFound;
        }

        var found = false;
        var bestCenterY = float.MinValue;
        BodyHandle bestCorpseBody = default;
        foreach (var rb in ragdollBones)
        {
            if (preferredCorpseBody.HasValue && rb.BodyHandle.Value != preferredCorpseBody.Value.Value)
                continue;
            if (rb.Mass < 0.45f ||
                BoneClasses.Cloth.Matches(rb.Name) ||
                BoneClasses.SoftTissue.Matches(rb.Name) ||
                BoneClasses.Weapon.Matches(rb.Name))
                continue;

            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            if (rb.ColliderShape == RagdollColliderShape.Box)
            {
                if (TryGetOrientedBoxSurfaceHeight(
                        body.Pose, rb.BoxHalfExtents, sphereCenter, 0f, out var boxSurfaceY))
                {
                    // Retain a slight physical overlap later in the root correction; this is the
                    // geometrically visible top plus the contacting NPC sphere's own radius.
                    var candidate = boxSurfaceY + sphereRadius;
                    if (!found || candidate > bestCenterY)
                    {
                        bestCenterY = candidate;
                        bestCorpseBody = rb.BodyHandle;
                        found = true;
                    }
                }
                continue;
            }

            var axis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
            var axisXz = new Vector2(axis.X, axis.Z);
            var relativeXz = new Vector2(body.Pose.Position.X - sphereCenter.X, body.Pose.Position.Z - sphereCenter.Z);
            var halfLength = MathF.Max(0f, rb.CapsuleHalfLength);
            var combinedRadius = MathF.Max(0.0001f, rb.CapsuleRadius + sphereRadius);
            var projected = 0f;
            var axisXzLengthSq = axisXz.LengthSquared();
            if (axisXzLengthSq > 1e-6f)
                projected = -Vector2.Dot(relativeXz, axisXz) / axisXzLengthSq;
            projected = Math.Clamp(projected, -halfLength, halfLength);

            void Consider(float alongAxis)
            {
                var sampleXz = relativeXz + axisXz * alongAxis;
                var horizontalSq = sampleXz.LengthSquared();
                if (horizontalSq > combinedRadius * combinedRadius)
                    return;

                var rise = MathF.Sqrt(MathF.Max(0f, combinedRadius * combinedRadius - horizontalSq));
                // Side contacts are collision, not support. Requiring a meaningful upward normal
                // keeps wide spiders from treating a corpse hugged between their legs as a floor.
                var minimumUpwardNormal = maintainContact ? 0.28f : 0.45f;
                if (rise / combinedRadius < minimumUpwardNormal)
                    return;
                var candidate = body.Pose.Position.Y + alongAxis * axis.Y + rise;
                if (!found || candidate > bestCenterY)
                {
                    bestCenterY = candidate;
                    bestCorpseBody = rb.BodyHandle;
                    found = true;
                }
            }

            Consider(projected);
            Consider(-halfLength);
            Consider(halfLength);
            Consider(Math.Clamp(projected - halfLength * 0.35f, -halfLength, halfLength));
            Consider(Math.Clamp(projected + halfLength * 0.35f, -halfLength, halfLength));
        }

        if (found)
            hit = new NpcTraversalSurfaceHit(bestCenterY, bestCorpseBody);
        return found;
    }

    private static bool IsTraversalSurfaceBone(RagdollBone rb)
        => rb.Mass >= 0.45f &&
           !BoneClasses.Cloth.Matches(rb.Name) &&
           !BoneClasses.SoftTissue.Matches(rb.Name) &&
           !BoneClasses.Weapon.Matches(rb.Name);

    // Navigation landmarks (where walkers aim on a lying body) are profile data: semantic, readable
    // parts of a humanoid pose rather than a second copy of the collider list. See
    // SkeletonProfile.StompLandmarks; mod-specific soft bones there are never promoted to traversal
    // surfaces by IsTraversalSurfaceBone.
    private static ImmutableArray<StompLandmark> CorpseTraversalLandmarks => HumanoidSkeleton.StompLandmarks;

    // Face bones are classified (eye / nose / mouth) and ranked by the words they are named with:
    // profile data (SkeletonProfile.FaceLandmarks).
    private static int FaceLandmarkCategory(string boneName)
        => HumanoidSkeleton.FaceLandmarks.Category(boneName);

    private static int FaceLandmarkSpecificity(string boneName)
        => HumanoidSkeleton.FaceLandmarks.SpecificityOf(boneName);

    private void CaptureFaceTraversalLandmarks(ISkeletonPose skeleton)
    {
        faceTraversalLandmarks.Clear();
        if (simulation == null || targetCharacterAddress == nint.Zero)
            return;

        RagdollBone? headBone = null;
        foreach (var bone in ragdollBones)
        {
            if (!bone.Name.Equals(Bones.Head, StringComparison.Ordinal))
                continue;
            headBone = bone;
            break;
        }
        if (!headBone.HasValue)
            return;

        // The target's skeleton as InitializePhysics read it this frame.
        if (!skeleton.HasRoot)
            return;

        var rb = headBone.Value;
        var headBody = simulation.Bodies.GetBodyReference(rb.BodyHandle);
        var profileOrigin = headBody.Pose.Position -
                            Vector3.Transform(rb.ShapeCenterOffset, headBody.Pose.Orientation);
        var segmentAxis = Vector3.Transform(Vector3.UnitY, headBody.Pose.Orientation);
        var headOrigin = profileOrigin - segmentAxis * rb.SegmentHalfLength;
        var headRotation = Quaternion.Normalize(headBody.Pose.Orientation * rb.CapsuleToBoneOffset);
        var headRotationInverse = Quaternion.Inverse(headRotation);

        var skeletonRoot = skeleton.Root;
        var skeletonPosition = skeletonRoot.Position;
        var skeletonRotation = skeletonRoot.Rotation;
        var best = new (Vector3 WorldPosition, int Score)?[4];
        for (var partialIndex = 1; partialIndex < skeleton.PartCount; partialIndex++)
        {
            if (!skeleton.PartAvailable(partialIndex) || !skeleton.PartInSync(partialIndex))
                continue;

            var pose = skeleton.PartModelPose(partialIndex);
            var boneCount = Math.Min(pose.Length, skeleton.PartBoneNameCount(partialIndex));
            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var name = skeleton.PartBoneName(partialIndex, boneIndex);
                if (string.IsNullOrEmpty(name))
                    continue;
                var category = FaceLandmarkCategory(name);
                if (category < 0)
                    continue;
                var score = FaceLandmarkSpecificity(name);
                if (best[category].HasValue && best[category]!.Value.Score >= score)
                    continue;

                ref var model = ref pose[boneIndex];
                var modelPosition = new Vector3(
                    model.Translation.X, model.Translation.Y, model.Translation.Z);
                best[category] = (
                    skeletonPosition + Vector3.Transform(modelPosition, skeletonRotation), score);
            }
        }

        for (var category = 0; category < best.Length; category++)
        {
            if (!best[category].HasValue)
                continue;
            var label = category switch
            {
                0 => "eyelash L",
                1 => "eyelash R",
                2 => "nose",
                _ => "mouth",
            };
            var local = Vector3.Transform(
                best[category]!.Value.WorldPosition - headOrigin, headRotationInverse);
            // IDs depend only on the semantic category, so they cannot disappear or change when
            // a partial skeleton rebuilds or a more specific mod bone happens to be enumerated.
            faceTraversalLandmarks.Add(new FaceTraversalLandmark(
                unchecked(int.MinValue + category), local, label));
        }
    }

    /// <summary>Build pose-following navigation slots from semantic body landmarks. Landmark X/Z
    /// tracks the named bone, while actual root height is resolved separately by the traversal
    /// query. Soft/body-mod landmarks therefore never become colliders, and clothing cannot win
    /// merely because it is the nearest large mesh.</summary>
    public int CollectCorpseTraversalSlots(List<CorpseTraversalSlot> destination)
    {
        destination.Clear();
        if (simulation == null || !isActive || !physicsStarted)
            return 0;

        void TryAddLandmarkSlot(
            int id,
            Vector3 position,
            float clearance,
            float weight,
            float wanderRadius,
            bool isFace,
            string label,
            int supportBodyHandle = -1)
        {
            // The landmark chooses horizontal intent only. Autonomous swarm followers resolve
            // vertical support through their finite-mass physics carriers; the controlled monster
            // retains the explicit traversal query path. A spread hand/foot therefore remains a
            // navigation hint without becoming a teleport height.
            if (!isFace)
            foreach (var existing in destination)
            {
                var minimum = MathF.Max(clearance, existing.Clearance) * 0.62f;
                var delta = new Vector2(position.X - existing.Position.X, position.Z - existing.Position.Z);
                if (delta.LengthSquared() < minimum * minimum)
                    return;
            }

            destination.Add(new CorpseTraversalSlot(
                id, position, clearance, weight, wanderRadius, isFace, label, supportBodyHandle));
        }

        for (var landmarkIndex = 0; landmarkIndex < CorpseTraversalLandmarks.Length; landmarkIndex++)
        {
            var landmark = CorpseTraversalLandmarks[landmarkIndex];
            RagdollBone? matched = null;
            foreach (var candidate in ragdollBones)
            {
                if (candidate.Name.Equals(landmark.Bone, StringComparison.Ordinal))
                {
                    matched = candidate;
                    break;
                }
            }
            if (!matched.HasValue)
                continue;

            var bone = matched.Value;
            var body = simulation.Bodies.GetBodyReference(bone.BodyHandle);
            var profileOrigin = body.Pose.Position -
                                Vector3.Transform(bone.ShapeCenterOffset, body.Pose.Orientation);
            var segmentAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
            // Runtime bodies sit at segment midpoints. Recover the actual skeleton joint so a
            // landmark follows the named bone rather than the middle of its collider.
            var boneOrigin = profileOrigin - segmentAxis * bone.SegmentHalfLength;
            var clearance = bone.ColliderShape == RagdollColliderShape.Box
                ? MathF.Max(bone.BoxHalfExtents.X, bone.BoxHalfExtents.Z)
                : bone.CapsuleRadius;
            clearance = Math.Clamp(clearance, 0.06f, 0.18f);

            // A broad load-bearing landmark is a surface region, not a single parking meter.
            // Giving it one exclusive reservation made the apparent climb capacity equal to the
            // number of surviving bone names; body mods then collapsed several names onto the same
            // point and left most of a large swarm waiting on terrain.  Generate stable lateral
            // lanes from the collider's real, scaled frame.  The normal de-duplication below still
            // removes lanes that overlap another body region, so this increases usable surface
            // capacity without allowing every follower to converge on one bone.
            var slotBaseId = unchecked(bone.BodyHandle.Value * 256 + landmarkIndex * 4);
            TryAddLandmarkSlot(
                slotBaseId, boneOrigin, clearance,
                landmark.Weight, landmark.WanderRadius, false, landmark.Label);

            if (landmark.Weight >= 10f)
            {
                var localX = Vector3.Transform(Vector3.UnitX, body.Pose.Orientation);
                var localZ = Vector3.Transform(Vector3.UnitZ, body.Pose.Orientation);
                localX.Y = 0f;
                localZ.Y = 0f;
                var lateral = localX.LengthSquared() >= localZ.LengthSquared() ? localX : localZ;
                if (lateral.LengthSquared() > 0.0001f)
                {
                    lateral = Vector3.Normalize(lateral);
                    var spread = Math.Clamp(
                        MathF.Max(clearance * 0.9f, landmark.WanderRadius * 0.8f), 0.075f, 0.18f);
                    var laneWander = MathF.Min(landmark.WanderRadius * 0.35f, spread * 0.3f);
                    TryAddLandmarkSlot(
                        slotBaseId + 1, boneOrigin + lateral * spread, clearance,
                        landmark.Weight * 0.92f, laneWander, false, landmark.Label + " L");
                    TryAddLandmarkSlot(
                        slotBaseId + 2, boneOrigin - lateral * spread, clearance,
                        landmark.Weight * 0.92f, laneWander, false, landmark.Label + " R");
                }
            }
        }

        // Face definitions were captured once from the coherent initialization snapshot. From
        // here on they are transformed exclusively by the BEPU j_kao body, so face and torso
        // landmarks belong to the same physics tick and keep stable identities for the whole sim.
        var preciseFaceCount = 0;
        RagdollBone? faceSupportBone = null;
        foreach (var bone in ragdollBones)
        {
            if (!bone.Name.Equals(Bones.Head, StringComparison.Ordinal))
                continue;
            faceSupportBone = bone;
            break;
        }

        if (faceSupportBone.HasValue && faceTraversalLandmarks.Count > 0)
        {
            var bone = faceSupportBone.Value;
            var body = simulation.Bodies.GetBodyReference(bone.BodyHandle);
            var profileOrigin = body.Pose.Position -
                                Vector3.Transform(bone.ShapeCenterOffset, body.Pose.Orientation);
            var segmentAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
            var headOrigin = profileOrigin - segmentAxis * bone.SegmentHalfLength;
            var headRotation = Quaternion.Normalize(body.Pose.Orientation * bone.CapsuleToBoneOffset);
            foreach (var face in faceTraversalLandmarks)
            {
                var before = destination.Count;
                var position = headOrigin + Vector3.Transform(face.HeadLocalPosition, headRotation);
                TryAddLandmarkSlot(
                    face.Id, position, 0.05f, 3f, 0.025f, true, face.Label,
                    bone.BodyHandle.Value);
                if (destination.Count > before)
                    preciseFaceCount++;
            }
        }

        if (preciseFaceCount == 0)
        {
            foreach (var bone in ragdollBones)
            {
                if (!bone.Name.Equals(Bones.Head, StringComparison.Ordinal))
                    continue;
                var body = simulation.Bodies.GetBodyReference(bone.BodyHandle);
                var profileOrigin = body.Pose.Position -
                                    Vector3.Transform(bone.ShapeCenterOffset, body.Pose.Orientation);
                var segmentAxis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
                var faceOrigin = profileOrigin - segmentAxis * bone.SegmentHalfLength;
                TryAddLandmarkSlot(
                    unchecked(bone.BodyHandle.Value * 64 + 63), faceOrigin, 0.055f,
                    2.5f, 0.02f, true, "face", bone.BodyHandle.Value);
                break;
            }
        }

        return destination.Count;
    }

    /// <summary>
    /// Finds the capsule surface under a circular foot probe. This is intentionally an
    /// approximation of the structural ragdoll bodies rather than a general physics raycast:
    /// cloth, soft tissue and tiny decorative bodies cannot become accidental stairs, and the
    /// caller can impose a low maximum climb height relative to terrain.
    /// </summary>
    public bool TryGetWalkableSurfaceHeight(Vector3 point, float probeRadius, out float surfaceY)
    {
        surfaceY = float.MinValue;
        if (simulation == null || !isActive || !physicsStarted)
            return false;

        probeRadius = Math.Clamp(probeRadius, 0f, 0.5f);
        if (characterSurfaceRuntime != null)
        {
            var profileFound = false;
            var profileSurfaceY = float.MinValue;
            var queryRadius = MathF.Max(0.001f, probeRadius);
            foreach (var rb in ragdollBones)
            {
                if (!IsTraversalSurfaceBone(rb))
                    continue;
                if (!characterSurfaceRuntime.TryGetVerticalSupport(
                        point, queryRadius, rb.Name, out var requiredCenterY, out _))
                    continue;
                var candidate = requiredCenterY - queryRadius;
                if (candidate <= profileSurfaceY)
                    continue;
                profileSurfaceY = candidate;
                profileFound = true;
            }
            surfaceY = profileSurfaceY;
            return profileFound;
        }

        var bestSurfaceY = float.MinValue;
        var found = false;
        foreach (var rb in ragdollBones)
        {
            if (rb.Mass < 0.45f ||
                BoneClasses.Cloth.Matches(rb.Name) ||
                BoneClasses.SoftTissue.Matches(rb.Name) ||
                BoneClasses.Weapon.Matches(rb.Name))
                continue;

            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var center = body.Pose.Position;

            if (rb.ColliderShape == RagdollColliderShape.Box)
            {
                if (TryGetOrientedBoxSurfaceHeight(
                        body.Pose, rb.BoxHalfExtents, point, probeRadius, out var boxSurfaceY) &&
                    (!found || boxSurfaceY > bestSurfaceY))
                {
                    bestSurfaceY = boxSurfaceY;
                    found = true;
                }
                continue;
            }

            var axis = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
            var axisXz = new Vector2(axis.X, axis.Z);
            var relativeXz = new Vector2(center.X - point.X, center.Z - point.Z);
            var halfLength = MathF.Max(0f, rb.CapsuleHalfLength);
            var radius = MathF.Max(0.005f, rb.CapsuleRadius);

            var projected = 0f;
            var axisXzLengthSq = axisXz.LengthSquared();
            if (axisXzLengthSq > 1e-6f)
                projected = -Vector2.Dot(relativeXz, axisXz) / axisXzLengthSq;
            projected = Math.Clamp(projected, -halfLength, halfLength);

            void ConsiderCandidate(float alongAxis)
            {
                var sampleXz = relativeXz + axisXz * alongAxis;
                // Minkowski-expand horizontally by the foot radius, but retain the body's real
                // vertical radius. This starts the climb just before the actor's center reaches
                // the corpse without inventing an oversized vertical step.
                var effectiveHorizontal = MathF.Max(0f, sampleXz.Length() - probeRadius);
                if (effectiveHorizontal > radius)
                    return;

                var rise = MathF.Sqrt(MathF.Max(0f, radius * radius - effectiveHorizontal * effectiveHorizontal));
                var candidateY = center.Y + alongAxis * axis.Y + rise;
                if (!found || candidateY > bestSurfaceY)
                {
                    bestSurfaceY = candidateY;
                    found = true;
                }
            }

            ConsiderCandidate(projected);
            ConsiderCandidate(-halfLength);
            ConsiderCandidate(halfLength);
            ConsiderCandidate(Math.Clamp(projected - halfLength * 0.35f, -halfLength, halfLength));
            ConsiderCandidate(Math.Clamp(projected + halfLength * 0.35f, -halfLength, halfLength));
        }

        surfaceY = bestSurfaceY;
        return found;
    }

    /// <summary>
    /// Vertical foot probes against the actual oriented box used by BEPU. Sampling the center and
    /// rim of the foot disk gives early support without replacing a rotated box by an oversized
    /// world AABB (which would make actors float above its corners).
    /// </summary>
    private static bool TryGetOrientedBoxSurfaceHeight(
        RigidPose pose,
        Vector3 halfExtents,
        Vector3 point,
        float probeRadius,
        out float surfaceY)
    {
        surfaceY = float.MinValue;
        if (halfExtents.X <= 0f || halfExtents.Y <= 0f || halfExtents.Z <= 0f)
            return false;

        var orientation = Quaternion.Normalize(pose.Orientation);
        var inverse = Quaternion.Inverse(orientation);
        var axisX = Vector3.Transform(Vector3.UnitX, orientation);
        var axisY = Vector3.Transform(Vector3.UnitY, orientation);
        var axisZ = Vector3.Transform(Vector3.UnitZ, orientation);
        var verticalExtent =
            MathF.Abs(axisX.Y) * halfExtents.X +
            MathF.Abs(axisY.Y) * halfExtents.Y +
            MathF.Abs(axisZ.Y) * halfExtents.Z;
        var rayOriginY = pose.Position.Y + verticalExtent + 0.02f;
        var localDirection = Vector3.Transform(-Vector3.UnitY, inverse);
        var diagonal = probeRadius * 0.70710678f;
        Span<Vector2> offsets = stackalloc Vector2[9]
        {
            Vector2.Zero,
            new(probeRadius, 0f), new(-probeRadius, 0f),
            new(0f, probeRadius), new(0f, -probeRadius),
            new(diagonal, diagonal), new(-diagonal, diagonal),
            new(diagonal, -diagonal), new(-diagonal, -diagonal),
        };

        var found = false;
        foreach (var offset in offsets)
        {
            var worldOrigin = new Vector3(point.X + offset.X, rayOriginY, point.Z + offset.Y);
            var localOrigin = Vector3.Transform(worldOrigin - pose.Position, inverse);
            var tMin = 0f;
            var tMax = float.MaxValue;
            if (!IntersectRayBoxSlab(localOrigin.X, localDirection.X, halfExtents.X, ref tMin, ref tMax) ||
                !IntersectRayBoxSlab(localOrigin.Y, localDirection.Y, halfExtents.Y, ref tMin, ref tMax) ||
                !IntersectRayBoxSlab(localOrigin.Z, localDirection.Z, halfExtents.Z, ref tMin, ref tMax))
                continue;

            var candidateY = rayOriginY - tMin;
            if (!found || candidateY > surfaceY)
            {
                surfaceY = candidateY;
                found = true;
            }
        }

        return found;
    }

    private static bool IntersectRayBoxSlab(
        float origin,
        float direction,
        float extent,
        ref float tMin,
        ref float tMax)
    {
        if (MathF.Abs(direction) < 1e-6f)
            return MathF.Abs(origin) <= extent;

        var t1 = (-extent - origin) / direction;
        var t2 = (extent - origin) / direction;
        if (t1 > t2)
            (t1, t2) = (t2, t1);

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax && tMax >= 0f;
    }

    /// <summary>
    /// Queue a finite-mass traversal carrier for an autonomous follower. The request is consumed
    /// by the render/physics owner before the next timestep; callers never mutate BEPU from
    /// Framework.Update. The carrier is a foot-volume, not a duplicate full character collider.
    /// </summary>
    private void DriveNpcTraversalProxyCore(
        nint address,
        Vector3 initialRoot,
        Vector3 targetRoot,
        float floorY,
        float visualScale,
        float moveSpeed,
        float maxClimb,
        float stompResponse,
        int concurrentStompers,
        bool locomoting)
    {
        if (address == nint.Zero)
            return;
        lock (npcTraversalProxySync)
        {
            npcTraversalProxyRequests[address] = new NpcTraversalProxyRequest
            {
                Enabled = true,
                InitialRoot = initialRoot,
                TargetRoot = targetRoot,
                FloorY = floorY,
                VisualScale = Math.Clamp(visualScale, 0.2f, 4f),
                MoveSpeed = Math.Clamp(moveSpeed, 0.1f, 12f),
                MaxClimb = Math.Clamp(maxClimb, 0.1f, 3f),
                StompResponse = Math.Clamp(stompResponse, 0f, 4f),
                ConcurrentStompers = Math.Clamp(concurrentStompers, 1, 128),
                Locomoting = locomoting,
            };
        }
    }

    private void ReleaseNpcTraversalProxyCore(nint address)
    {
        if (address == nint.Zero)
            return;
        lock (npcTraversalProxySync)
        {
            if (npcTraversalProxyRequests.TryGetValue(address, out var request))
            {
                request.Enabled = false;
                npcTraversalProxyRequests[address] = request;
            }
            else
            {
                npcTraversalProxyRequests[address] = new NpcTraversalProxyRequest { Enabled = false };
            }
        }
    }

    public bool TryGetNpcTraversalProxyRoot(
        nint address,
        out Vector3 root,
        out bool onCorpseSupport)
    {
        lock (npcTraversalProxySync)
        {
            if (npcTraversalProxies.TryGetValue(address, out var proxy) &&
                proxy.Active && proxy.HasCachedRoot)
            {
                root = proxy.CachedRoot;
                onCorpseSupport = proxy.OnCorpseSupport;
                return true;
            }
        }
        root = Vector3.Zero;
        onCorpseSupport = false;
        return false;
    }

    private void UpdateNpcTraversalProxies(float dt)
    {
        npcTraversalProxyActivityThisFrame = false;
        if (simulation == null || bufferPool == null)
            return;

        KeyValuePair<nint, NpcTraversalProxyRequest>[] requests;
        lock (npcTraversalProxySync)
            requests = npcTraversalProxyRequests.ToArray();

        lock (npcTraversalProxySync)
        foreach (var (address, request) in requests)
        {
            if (!request.Enabled)
            {
                if (!npcTraversalProxies.TryGetValue(address, out var parked) || !parked.Active)
                    continue;
                var parkedBody = simulation.Bodies.GetBodyReference(parked.Body);
                parkedBody.Pose.Position = new Vector3(0f, -500f, 0f);
                parkedBody.Velocity.Linear = Vector3.Zero;
                parkedBody.Velocity.Angular = Vector3.Zero;
                parkedBody.Awake = false;
                parked.Active = false;
                parked.HasCachedRoot = false;
                parked.OnCorpseSupport = false;
                continue;
            }

            if (!npcTraversalProxies.TryGetValue(address, out var proxy))
            {
                // Geometric scale determines both contact patch and mass. Cubic mass scaling is
                // the same model for every actor size; clamps only protect the solver from corrupt
                // draw scales, rather than introducing a special small-monster behaviour.
                var radius = Math.Clamp(0.18f * request.VisualScale, 0.055f, 0.55f);
                var mass = Math.Clamp(12f * request.VisualScale * request.VisualScale * request.VisualScale,
                    1.5f, 100f);
                var sphere = new Sphere(radius);
                var shape = simulation.Shapes.Add(sphere);
                var inertia = sphere.ComputeInertia(mass);
                var body = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(request.InitialRoot + Vector3.UnitY * radius, Quaternion.Identity),
                    inertia,
                    new CollidableDescription(shape, MathF.Min(0.025f, radius * 0.2f)),
                    new BodyActivityDescription(0.01f)));
                proxy = new NpcTraversalProxy
                {
                    Body = body,
                    Shape = shape,
                    Radius = radius,
                    Active = true,
                    CachedRoot = request.InitialRoot,
                    HasCachedRoot = true,
                    Mass = mass,
                    // Keep a crowd from landing every step on the same physics tick.
                    GaitPhase = ((uint)address.GetHashCode() & 0xffffu) / 65535f,
                };
                npcTraversalProxies[address] = proxy;
                npcTraversalDynamicBodyHandles.Add(body.Value);
                prevAllAsleep = false;
            }

            var bodyRef = simulation.Bodies.GetBodyReference(proxy.Body);
            if (!proxy.Active)
            {
                bodyRef.Pose.Position = request.InitialRoot + Vector3.UnitY * proxy.Radius;
                bodyRef.Velocity.Linear = Vector3.Zero;
                bodyRef.Velocity.Angular = Vector3.Zero;
                bodyRef.Awake = true;
                proxy.Active = true;
            }

            var root = bodyRef.Pose.Position - Vector3.UnitY * proxy.Radius;
            var toTarget = new Vector2(request.TargetRoot.X - root.X, request.TargetRoot.Z - root.Z);
            var distance = toTarget.Length();
            var direction = distance > 1e-5f ? toTarget / distance : Vector2.Zero;

            // Look far enough ahead to begin stepping up before the carrier reaches the corpse's
            // side wall. The old one-radius probe was especially bad for small creatures: their
            // carrier hit the body before the probe overlapped its top surface, so the horizontal
            // motor kept walking into/around the corpse without ever asking for vertical motion.
            var probeLookAhead = MathF.Max(
                proxy.Radius * 1.5f + 0.035f,
                MathF.Min(0.55f, request.MoveSpeed * 0.16f));
            var probeAdvance = MathF.Min(distance, probeLookAhead);
            var probe = new Vector3(
                root.X + direction.X * probeAdvance,
                root.Y,
                root.Z + direction.Y * probeAdvance);
            var hasSurface = TryGetWalkableSurfaceHeight(
                probe, MathF.Max(0.045f, proxy.Radius * 0.9f), out var surfaceY);
            var climbHeight = surfaceY - root.Y;
            var canClimb = hasSurface && surfaceY > request.FloorY + 0.025f &&
                           surfaceY <= request.FloorY + request.MaxClimb && climbHeight > 0.018f;

            // Do not let the horizontal motor outrun the vertical step. At normal run speed a
            // creature could cross a prone torso in less time than the former 0.55m/s lift took to
            // clear it. Retaining a little forward intent lets the dynamic carrier roll over the
            // edge instead of jumping vertically in place.
            var horizontalScale = canClimb
                ? Math.Clamp(0.16f + (request.MaxClimb - climbHeight) * 0.35f, 0.16f, 0.42f)
                : 1f;
            var desiredSpeed = MathF.Min(request.MoveSpeed * horizontalScale, distance * 4f);
            var desiredVelocity = direction * desiredSpeed;
            var currentVelocity = new Vector2(bodyRef.Velocity.Linear.X, bodyRef.Velocity.Linear.Z);
            var velocityDelta = desiredVelocity - currentVelocity;
            var horizontalAcceleration = canClimb ? 18f : 8f;
            var maxHorizontalDelta = horizontalAcceleration * Math.Clamp(dt, 0f, 0.05f);
            if (velocityDelta.LengthSquared() > maxHorizontalDelta * maxHorizontalDelta)
                velocityDelta = Vector2.Normalize(velocityDelta) * maxHorizontalDelta;
            bodyRef.Velocity.Linear.X += velocityDelta.X;
            bodyRef.Velocity.Linear.Z += velocityDelta.Y;
            bodyRef.Velocity.Angular *= MathF.Exp(-8f * Math.Clamp(dt, 0f, 0.05f));

            // Position remains fully dynamic and the solver still decides whether the carrier
            // clears the edge or transfers force into the corpse. This is a motor, not a root warp.
            if (canClimb)
            {
                var desiredUp = Math.Clamp(1.0f + climbHeight * 5f, 1.0f, 3.8f);
                var maxVerticalDelta = 20f * Math.Clamp(dt, 0f, 0.05f);
                bodyRef.Velocity.Linear.Y += Math.Clamp(
                    desiredUp - bodyRef.Velocity.Linear.Y, -maxVerticalDelta, maxVerticalDelta);
            }

            var supportedNow = hasSurface && surfaceY > request.FloorY + 0.025f &&
                               MathF.Abs(root.Y - surfaceY) <= MathF.Max(0.16f, proxy.Radius);
            proxy.OnCorpseSupport = supportedNow;

            // The carrier is deliberately a smooth sphere, not twenty animated infinite-mass bone
            // colliders. That makes a large swarm stable, but it also erased the gait: sliding a
            // constant load across a body produces almost no visible local response. Reintroduce
            // only the missing signal as bounded, alternating physical impulses through the carrier.
            // The corpse still receives these through the contact solver at the real support point.
            var stompedThisFrame = false;
            if (supportedNow && request.StompResponse > 0.001f)
            {
                var stepRate = request.Locomoting
                    ? Math.Clamp(1.55f + desiredSpeed * 0.34f, 1.55f, 3.4f)
                    : 0.62f;
                var previousStep = (int)MathF.Floor(proxy.GaitPhase);
                proxy.GaitPhase += stepRate * Math.Clamp(dt, 0f, 0.05f);
                var currentStep = (int)MathF.Floor(proxy.GaitPhase);
                if (currentStep != previousStep)
                {
                    // At most eight full-strength feet contribute at once. Beyond that each carrier
                    // receives a smaller share, preventing a 30-member swarm from scaling the total
                    // impulse without bound while keeping contacts spatially distributed.
                    var crowdShare = MathF.Min(1f, 8f / request.ConcurrentStompers);
                    var response = request.StompResponse * crowdShare;
                    var effectiveFootMass = Math.Clamp(MathF.Max(proxy.Mass, 6f), 6f, 24f);
                    var speedLoad = request.Locomoting
                        ? 0.28f + MathF.Min(0.14f, desiredSpeed * 0.035f)
                        : 0.075f;
                    var downwardImpulse = effectiveFootMass * speedLoad * response;
                    var downVelocity = Math.Clamp(downwardImpulse / MathF.Max(0.5f, proxy.Mass), 0f, 2.2f);
                    bodyRef.Velocity.Linear.Y -= downVelocity;

                    // Alternate a small tangential weight shift. It creates local twist rather than
                    // merely translating the whole corpse, and the position motor naturally brings
                    // the carrier back over its reserved landmark after each step.
                    Vector2 tangent;
                    if (direction.LengthSquared() > 0.001f)
                    {
                        tangent = new Vector2(-direction.Y, direction.X);
                    }
                    else
                    {
                        var stableAngle = (((uint)address.GetHashCode() >> 16) & 0xffffu) /
                                          65535f * MathF.Tau;
                        tangent = new Vector2(MathF.Cos(stableAngle), MathF.Sin(stableAngle));
                    }
                    var side = (currentStep & 1) == 0 ? 1f : -1f;
                    var lateralImpulse = downwardImpulse * (request.Locomoting ? 0.22f : 0.10f);
                    var lateralVelocity = Math.Clamp(
                        lateralImpulse / MathF.Max(0.5f, proxy.Mass), 0f, 0.48f);
                    bodyRef.Velocity.Linear.X += tangent.X * lateralVelocity * side;
                    bodyRef.Velocity.Linear.Z += tangent.Y * lateralVelocity * side;
                    stompedThisFrame = true;
                }

                // Avoid precision loss after very long Enemy Control sessions without changing the
                // alternating parity of the next footfall.
                if (proxy.GaitPhase >= 4096f)
                    proxy.GaitPhase -= 4096f;
            }
            proxy.CachedRoot = root;
            proxy.HasCachedRoot = true;
            var activelyDriven = distance > MathF.Max(0.025f, proxy.Radius * 0.2f);
            if (activelyDriven || stompedThisFrame)
            {
                bodyRef.Awake = true;
                prevAllAsleep = false;
            }
            // An already-settling carrier still needs simulation ticks, but observing Awake must
            // not itself re-awaken it. Once BEPU puts the carrier to sleep at its target, the
            // ragdoll resting fast path can resume normally.
            npcTraversalProxyActivityThisFrame |= activelyDriven || stompedThisFrame || bodyRef.Awake;
        }
    }

    private void CacheNpcTraversalProxyPoses()
    {
        if (simulation == null)
            return;
        lock (npcTraversalProxySync)
        {
            foreach (var proxy in npcTraversalProxies.Values)
            {
                if (!proxy.Active)
                    continue;
                var body = simulation.Bodies.GetBodyReference(proxy.Body);
                proxy.CachedRoot = body.Pose.Position - Vector3.UnitY * proxy.Radius;
                proxy.HasCachedRoot = true;
            }
        }
    }
}
