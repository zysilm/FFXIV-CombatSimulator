// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.CollisionDetection;
using BepuUtilities.Memory;
using BepuSimulation = BepuPhysics.Simulation;

namespace CombatSimulator.Animation;

// Extension points of the ragdoll (stage A3b, docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §7). They replaced two partial
// methods of RagdollController, which cannot cross an assembly boundary. Written to move into the library in A4: no game types here. Register with RagdollController.AddExtension.

/// <summary>Called on the simulation thread once per fixed step, after the water forces and before the world's
/// Timestep. Setting <paramref name="handled"/> means the extension stepped the world itself (or chose not to): the
/// controller then skips its own Timestep, and later extensions are not called for this step.</summary>
public interface IRagdollStepExtension
{
    void OnSubstep(RagdollExtensionContext ctx, float dt, ref bool handled);
}

/// <summary>Called on the physics worker threads once per contact manifold, before the controller configures it.
/// Setting <paramref name="handled"/> means the extension configured the material: the controller's own
/// configuration is skipped, and later extensions are not called for this manifold. Implementations must only read
/// data that does not change during a step; <see cref="RagdollExtensionContext.Simulation"/> is the simulation the
/// pair belongs to.</summary>
public interface IRagdollContactExtension
{
    void ConfigureContact<TManifold>(RagdollExtensionContext ctx, CollidablePair pair, ref TManifold manifold,
        ref PairMaterialProperties material, ref bool handled) where TManifold : unmanaged, IContactManifold<TManifold>;
}

/// <summary>A ragdoll body as an extension sees it: the bone it stands for, its Bepu body and its collision
/// volume.</summary>
public readonly struct RagdollExtensionBone
{
    internal RagdollExtensionBone(string name, BodyHandle bodyHandle, RagdollController.RagdollColliderShape colliderShape,
        Vector3 boxHalfExtents, float capsuleHalfLength, float capsuleRadius)
    {
        Name = name;
        BodyHandle = bodyHandle;
        ColliderShape = colliderShape;
        BoxHalfExtents = boxHalfExtents;
        CapsuleHalfLength = capsuleHalfLength;
        CapsuleRadius = capsuleRadius;
    }

    public string Name { get; }
    public BodyHandle BodyHandle { get; }
    public RagdollController.RagdollColliderShape ColliderShape { get; }
    public Vector3 BoxHalfExtents { get; }
    public float CapsuleHalfLength { get; }
    public float CapsuleRadius { get; }
}

/// <summary>The ragdoll bodies of a controller, read-only and without copying the list.</summary>
public readonly struct RagdollExtensionBones : IReadOnlyList<RagdollExtensionBone>
{
    private readonly RagdollExtensionContext context;
    internal RagdollExtensionBones(RagdollExtensionContext context) { this.context = context; }

    public int Count => context.BoneCount;
    public RagdollExtensionBone this[int index] => context.GetBone(index);

    public IEnumerator<RagdollExtensionBone> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// What an extension can see and do inside one controller's simulation (one context per controller). Each member is
/// the controller's own state or operation, unchanged; the members are internal until they settle (the library
/// already has InternalsVisibleTo for that).
/// </summary>
public abstract class RagdollExtensionContext
{
    private protected RagdollExtensionContext() { }

    /// <summary>The controller's Bepu simulation (null before physics is built and after it is torn down). On the
    /// contact path this is the simulation the pair belongs to.</summary>
    internal abstract BepuSimulation? Simulation { get; }
    internal abstract BufferPool? BufferPool { get; }
    /// <summary>Bumped at every activation; external body and rig handles of an older generation are stale.</summary>
    internal abstract int ExternalBodyGeneration { get; }
    internal abstract bool IsActive { get; }

    internal abstract int BoneCount { get; }
    internal abstract RagdollExtensionBone GetBone(int index);
    internal RagdollExtensionBones Bones => new(this);

    /// <summary>The names of the bone definitions the controller builds from (a fresh array per call, as the
    /// controller's own definition table is built per call).</summary>
    internal abstract string[] GetBoneDefinitionNames();

    /// <summary>The actual triangle surface of a bone under a world-space point (see
    /// RagdollController.TryGetBoneVerticalSupport).</summary>
    internal abstract bool TryGetBoneVerticalSupport(string bone, Vector3 position, out Vector3 surface);

    /// <summary>One fixed step of the world, exactly as the controller does it when no extension handled the
    /// step.</summary>
    internal abstract void StepPhysicsSimulation(float dt);

    internal abstract void AttachWeaponContactBody(BodyHandle weapon);
    internal abstract void RemoveExternalRig(RagdollController.ExternalRigHandle? rig);
}

/// <summary>
/// The extensions registered on one controller. The narrow-phase callbacks of the controller's simulation hold a
/// reference to it (set when the callbacks are created), so extensions registered later are still seen. Arrays are
/// replaced, never changed in place.
/// </summary>
internal sealed class RagdollExtensionSet
{
    private volatile IRagdollStepExtension[] step = Array.Empty<IRagdollStepExtension>();
    private volatile IRagdollContactExtension[] contact = Array.Empty<IRagdollContactExtension>();
    private readonly object gate = new();

    /// <summary>Set before the first extension is published.</summary>
    internal RagdollExtensionContext? Context;

    internal IRagdollStepExtension[] Step => step;
    internal bool HasContact => contact.Length != 0;

    internal void Add(RagdollExtensionContext context, object extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var asStep = extension as IRagdollStepExtension;
        var asContact = extension as IRagdollContactExtension;
        if (asStep == null && asContact == null)
            throw new ArgumentException($"{extension.GetType().Name} implements neither {nameof(IRagdollStepExtension)} nor {nameof(IRagdollContactExtension)}", nameof(extension));
        lock (gate)
        {
            Context = context;
            if (asStep != null) step = Append(step, asStep);
            if (asContact != null) contact = Append(contact, asContact);
        }
    }

    /// <summary>In registration order; the first extension that sets <paramref name="handled"/> stops.</summary>
    internal void OnSubstep(float dt, ref bool handled)
    {
        var list = step;
        var context = Context!;
        for (var i = 0; i < list.Length && !handled; i++) list[i].OnSubstep(context, dt, ref handled);
    }

    /// <summary>In registration order; the first extension that sets <paramref name="handled"/> stops.</summary>
    internal void ConfigureContact<TManifold>(CollidablePair pair, ref TManifold manifold, ref PairMaterialProperties material,
        ref bool handled) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var list = contact;
        var context = Context!;
        for (var i = 0; i < list.Length && !handled; i++) list[i].ConfigureContact(context, pair, ref manifold, ref material, ref handled);
    }

    private static T[] Append<T>(T[] array, T item)
    {
        var result = new T[array.Length + 1];
        Array.Copy(array, result, array.Length);
        result[array.Length] = item;
        return result;
    }
}
