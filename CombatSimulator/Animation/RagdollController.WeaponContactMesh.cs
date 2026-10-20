// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using CombatSimulator.Animation.MeshVolume;

namespace CombatSimulator.Animation;

/// <summary>
/// Weapon-only body surfaces. Ragdoll bodies keep their lightweight volumes; each body with a
/// cached surface gets a kinematic mesh twin that follows it and collides only with registered
/// weapons. The weapon stops colliding with the volumes those twins replace. Weapons therefore
/// rest on the rendered body without the cost of full mesh ragdoll contacts. Twins have infinite
/// mass, so a weapon does not push the body.
/// </summary>
public unsafe partial class RagdollController
{
    private struct WeaponContactProxy
    {
        public int BoneListIndex;
        public BodyHandle RagdollBody;
        public BodyHandle Body;
        public TypedIndex Shape;
    }

    private static readonly Vector3 WeaponContactProxyParking = new(0, -9999, 0);

    private readonly List<WeaponContactProxy> weaponContactProxies = new();
    private readonly HashSet<int> weaponContactBodyHandles = new();
    private readonly HashSet<int> weaponContactProxyHandles = new();
    private readonly HashSet<int> weaponContactProxiedBodyHandles = new();

    /// <summary>Weapon body contacts use the cached body surface instead of the ragdoll volumes.</summary>
    internal void AttachWeaponContactBody(BodyHandle weapon)
    {
        if (simulation == null || !config.WeaponBodyMeshContacts || activeRagdollIsGeneric) return;
        if (weaponContactProxies.Count == 0) CreateWeaponContactProxies();
        if (weaponContactProxies.Count > 0) weaponContactBodyHandles.Add(weapon.Value);
    }

    internal void DetachWeaponContactBody(BodyHandle weapon)
    {
        if (!weaponContactBodyHandles.Remove(weapon.Value) || weaponContactBodyHandles.Count > 0) return;
        RemoveWeaponContactProxies();
    }

    private void CreateWeaponContactProxies()
    {
        if (simulation == null || bufferPool == null) return;
        var cache = GetWeaponContactMesh(targetCharacterAddress);
        if (cache == null) return;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            // Bodies that already collide through their rendered mesh need no twin.
            if (rb.BoneIndex < 0 || rb.MeshGeometry != null || !cache.TryGetCollisionGeometry(rb.Name, out var local)) continue;
            Mesh mesh;
            try { mesh = TwoSidedMesh.Create(local, bufferPool); }
            catch (Exception ex) { log.Warning(ex, $"Weapon contact surface rejected for {rb.Name}; weapon uses its volume."); continue; }
            TypedIndex shape;
            try { shape = simulation.Shapes.Add(mesh); }
            catch { mesh.Dispose(bufferPool); throw; }
            GetBonePoseFromBody(rb, out var origin, out var rotation, out _, out _);
            var body = simulation.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(origin, rotation), default,
                new CollidableDescription(shape, 0.04f), new BodyActivityDescription(0.01f)));
            weaponContactProxies.Add(new WeaponContactProxy { BoneListIndex = i, RagdollBody = rb.BodyHandle, Body = body, Shape = shape });
            weaponContactProxyHandles.Add(body.Value);
            weaponContactProxiedBodyHandles.Add(rb.BodyHandle.Value);
        }
        if (weaponContactProxies.Count > 0)
            log.Info($"Weapon contact surfaces: {weaponContactProxies.Count}/{ragdollBones.Count} ragdoll bodies.");
    }

    private void RemoveWeaponContactProxies()
    {
        if (simulation != null && bufferPool != null)
            foreach (var proxy in weaponContactProxies)
            {
                RemoveConstraintsOnBody(proxy.Body);
                try { simulation.Bodies.Remove(proxy.Body); }
                catch (Exception ex) { log.Warning(ex, $"RagdollController: failed to remove weapon contact surface {proxy.Body.Value}"); }
                try { simulation.Shapes.RemoveAndDispose(proxy.Shape, bufferPool); } catch { }
            }
        ClearWeaponContactProxyState();
    }

    // The simulation owns the native data; on teardown only the bookkeeping is dropped.
    private void ClearWeaponContactProxyState()
    {
        weaponContactProxies.Clear();
        weaponContactBodyHandles.Clear();
        weaponContactProxyHandles.Clear();
        weaponContactProxiedBodyHandles.Clear();
    }

    /// <summary>Before each step: twins start at their body's bone pose with the same rigid motion,
    /// so both integrate to the same place over the step.</summary>
    private void SyncWeaponContactProxies()
    {
        if (simulation == null || weaponContactProxies.Count == 0) return;
        foreach (var proxy in weaponContactProxies)
        {
            var twin = simulation.Bodies.GetBodyReference(proxy.Body);
            if (!TryFindRagdollBone(proxy.RagdollBody, proxy.BoneListIndex, out var rb))
            {
                // Its body was removed (e.g. a severed limb): retire the twin without a launch velocity.
                twin.Pose = new RigidPose(WeaponContactProxyParking, Quaternion.Identity);
                twin.Velocity = default;
                continue;
            }
            GetBonePoseFromBody(rb, out var origin, out var rotation, out var bodyPosition, out var velocity);
            twin.Pose = new RigidPose(origin, rotation);
            twin.Velocity = new BodyVelocity(velocity.Linear + Vector3.Cross(velocity.Angular, origin - bodyPosition), velocity.Angular);
            if (velocity.Linear.LengthSquared() + velocity.Angular.LengthSquared() > 1e-10f) twin.Awake = true;
        }
    }

    private bool TryFindRagdollBone(BodyHandle body, int hint, out RagdollBone bone)
    {
        if (hint < ragdollBones.Count && ragdollBones[hint].BodyHandle.Value == body.Value) { bone = ragdollBones[hint]; return true; }
        foreach (var candidate in ragdollBones)
            if (candidate.BodyHandle.Value == body.Value) { bone = candidate; return true; }
        bone = default;
        return false;
    }

    // Same body → bone-origin reconstruction as skeleton write-back.
    private void GetBonePoseFromBody(RagdollBone rb, out Vector3 origin, out Quaternion rotation,
        out Vector3 bodyPosition, out BodyVelocity velocity)
    {
        var body = simulation!.Bodies.GetBodyReference(rb.BodyHandle);
        bodyPosition = body.Pose.Position;
        velocity = body.Velocity;
        rotation = Quaternion.Normalize(body.Pose.Orientation * rb.CapsuleToBoneOffset);
        origin = bodyPosition - Vector3.Transform(rb.ShapeCenterOffset, body.Pose.Orientation);
        if (rb.SegmentHalfLength > 0)
            origin -= rb.SegmentHalfLength * Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
    }
}
