// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities.Memory;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

// Extension points (stage A3b, docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §7): registration, and the context through which
// an extension reaches this controller's simulation. Call sites: the fixed-step loop (Stepping.cs) and
// RagdollNarrowPhaseCallbacks.ConfigureContactManifold, each right after the old partial method and only when that
// did not handle it. With nothing registered each costs one length check.
public unsafe partial class RagdollController
{
    // Created with the controller; the narrow-phase callbacks of every simulation this controller builds point at it.
    private readonly RagdollExtensionSet extensionSet = new();
    private ExtensionContextImpl? extensionContext;

    internal RagdollExtensionContext ExtensionContext => extensionContext ??= new ExtensionContextImpl(this);

    /// <summary>Registers an extension implementing <see cref="IRagdollStepExtension"/>, <see cref="IRagdollContactExtension"/>
    /// or both. Extensions are called in registration order; the first one that sets handled stops the others.
    /// May be called before or after physics is built.</summary>
    internal void AddExtension(object extension) => extensionSet.Add(ExtensionContext, extension);

    private sealed class ExtensionContextImpl : RagdollExtensionContext
    {
        private readonly RagdollController owner;
        internal ExtensionContextImpl(RagdollController owner) { this.owner = owner; }

        internal override BepuSimulation? Simulation => owner.simulation;
        internal override BufferPool? BufferPool => owner.bufferPool;
        internal override int ExternalBodyGeneration => owner.externalBodyGeneration;
        internal override bool IsActive => owner.isActive;

        internal override int BoneCount => owner.ragdollBones.Count;

        internal override RagdollExtensionBone GetBone(int index)
        {
            var bone = owner.ragdollBones[index];
            return new RagdollExtensionBone(bone.Name, bone.BodyHandle, bone.ColliderShape, bone.BoxHalfExtents,
                bone.CapsuleHalfLength, bone.CapsuleRadius);
        }

        internal override string[] GetBoneDefinitionNames()
        {
            var defs = GetBoneDefs();
            var names = new string[defs.Length];
            for (var i = 0; i < defs.Length; i++) names[i] = defs[i].Name;
            return names;
        }

        internal override bool TryGetBoneVerticalSupport(string bone, Vector3 position, out Vector3 surface)
            => owner.TryGetBoneVerticalSupport(bone, position, out surface);

        internal override void StepPhysicsSimulation(float dt) => owner.StepPhysicsSimulation(dt);
        internal override void AttachWeaponContactBody(BodyHandle weapon) => owner.AttachWeaponContactBody(weapon);
        internal override void RemoveExternalRig(ExternalRigHandle? rig) => owner.RemoveExternalRig(rig);
    }
}
