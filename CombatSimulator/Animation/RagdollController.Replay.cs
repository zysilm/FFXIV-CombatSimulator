// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

// Entry points for the offline replay (Tests/RagdollReplay, stages A2a and A2b). A recording starts in the middle of
// a controller's life: what earlier activations left behind is in the Activate record and, since format version 3,
// in the MeshProduct "carried" record after it (the body surfaces); a version 2 recording does not list
// followWasActive, which its first FrameBegin record shows. The replay puts it back before it calls Activate.
// Nothing here runs in the game.
public unsafe partial class RagdollController
{
    /// <summary>One request of the walking-support proxy, as the Activate record lists it.</summary>
    internal readonly record struct ReplayTraversalRequest(
        nint Actor, bool Enabled, Vector3 InitialRoot, Vector3 TargetRoot, float FloorY, float VisualScale,
        float MoveSpeed, float MaxClimb, float StompResponse, int ConcurrentStompers, bool Locomoting);

    /// <summary>State a controller carries from one activation to the next (Activate record, format version 2).</summary>
    internal sealed class ReplayCarriedState
    {
        public bool PendingDirectedCollapse;
        public string PendingProfile = string.Empty;
        public float PendingDuration, PendingFootForce, PendingPelvisForce, PendingDrop, PendingForward, PendingPitchDegrees;
        public bool PendingEntryConditionedKneePowerLoss;
        public bool PendingKneePowerLoss;
        public bool UseLocalSeveredBones;
        public List<string> LocalSeveredBones = new();
        public List<nint> LightweightColliders = new();
        public uint ImpactRandomState;
        public int NextGrabSlotId;
        public int ExternalBodyGeneration;
        public List<int> StruckThisWindow = new();
        public List<nint> StrikeColliders = new();
        public List<ReplayTraversalRequest> TraversalRequests = new();
        /// <summary>Whether the previous activation's last frame wrote a follow position (public builds never reset it).</summary>
        public bool FollowWasActive;
        /// <summary>The body surfaces carried over from earlier activations (format version 3, MeshProduct "carried").</summary>
        public bool HasBodySurfaces;
        public nint FrozenMeshActor;
        public uint FrozenMeshEntity;
        public bool ObservedMeshEnabled;
        public int ObservedMeshCacheRevision = -1;
        public bool CachedMeshLodEnabled;
        public float CachedMeshLodTolerance;
        public long NextMeshCacheAttempt;
        public MeshVolume.FrozenMeshGeometryCache? FrozenMeshCache;
        public List<(nint Actor, uint Entity, nint Draw, MeshVolume.FrozenMeshGeometryCache? Cache)> WeaponContactMeshes = new();
    }

    internal void RestoreCarriedState(ReplayCarriedState s)
    {
        pendingDirectedCollapseSpike = s.PendingDirectedCollapse;
        pendingDirectedProfileId = s.PendingProfile;
        pendingDirectedDuration = s.PendingDuration;
        pendingDirectedFootForce = s.PendingFootForce;
        pendingDirectedPelvisForce = s.PendingPelvisForce;
        pendingDirectedDrop = s.PendingDrop;
        pendingDirectedForward = s.PendingForward;
        pendingDirectedPitchDegrees = s.PendingPitchDegrees;
        pendingEntryConditionedKneePowerLoss = s.PendingEntryConditionedKneePowerLoss;
        pendingKneePowerLossPattern = s.PendingKneePowerLoss;
        useLocalDismemberBones = s.UseLocalSeveredBones;
        localDismemberBones.Clear();
        localDismemberBones.AddRange(s.LocalSeveredBones);
        lightweightNpcCollisionAddresses.Clear();
        foreach (var address in s.LightweightColliders) lightweightNpcCollisionAddresses.Add(address);
        impactRandomState = s.ImpactRandomState;
        nextGrabSlotId = s.NextGrabSlotId;
        externalBodyGeneration = s.ExternalBodyGeneration;
        struckThisWindow.Clear();
        foreach (var handle in s.StruckThisWindow) struckThisWindow.Add(handle);
        strikeColliderAddresses.Clear();
        foreach (var address in s.StrikeColliders) strikeColliderAddresses.Add(address);
        lock (npcTraversalProxySync)
        {
            npcTraversalProxyRequests.Clear();
            foreach (var r in s.TraversalRequests)
                npcTraversalProxyRequests[r.Actor] = new NpcTraversalProxyRequest
                {
                    Enabled = r.Enabled, InitialRoot = r.InitialRoot, TargetRoot = r.TargetRoot, FloorY = r.FloorY,
                    VisualScale = r.VisualScale, MoveSpeed = r.MoveSpeed, MaxClimb = r.MaxClimb,
                    StompResponse = r.StompResponse, ConcurrentStompers = r.ConcurrentStompers, Locomoting = r.Locomoting,
                };
        }
        followWasActive = s.FollowWasActive;
        if (s.HasBodySurfaces)
        {
            frozenMeshActor = s.FrozenMeshActor;
            frozenMeshEntity = s.FrozenMeshEntity;
            observedMeshEnabled = s.ObservedMeshEnabled;
            observedMeshCacheRevision = s.ObservedMeshCacheRevision;
            cachedMeshLodEnabled = s.CachedMeshLodEnabled;
            cachedMeshLodTolerance = s.CachedMeshLodTolerance;
            nextMeshCacheAttempt = s.NextMeshCacheAttempt;
            frozenMeshCache = s.FrozenMeshCache;
            weaponContactMeshes.Clear();
            weaponContactMeshOrder.Clear();
            foreach (var entry in s.WeaponContactMeshes)
            {
                weaponContactMeshes[entry.Actor] = (entry.Entity, entry.Draw, entry.Cache);
                weaponContactMeshOrder.Add(entry.Actor);
            }
        }
    }

    /// <summary>One render frame, as the render hook runs it.</summary>
    internal void ReplayRenderFrame() => OnRenderFrame();

    /// <summary>True while the controller is armed or simulating.</summary>
    internal bool ReplayActive => isActive;
}
