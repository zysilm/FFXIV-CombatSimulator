// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    // ─── Direct impulse ──────────────────────────────────────────────────────

    private void ApplyImpulseCore(string boneName, Vector3 velocityDelta)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Linear += velocityDelta;
        body.Awake = true;
        BeginBiomechanicalSettle();
    }

    private const float RecoilRelaxDuration = 0.45f;
    private const float RecoilSwingAngle = 1.7f; // ~97° — wide arc during the recoil window
    private struct RecoilRelax { public string Bone; public float Remaining; }

    /// <summary>Add an angular velocity to a bone's body (a rotational kick) and briefly release its
    /// joint (drop the motor, widen the swing limit) so the stump above a severed cut actually swings
    /// up instead of being held in place. Used for the body-side recoil when a limb is kicked off.</summary>
    private void ApplyAngularVelocityCore(string boneName, Vector3 angularVelocityDelta)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Angular += angularVelocityDelta;
        body.Awake = true;
        RelaxJointForRecoil(boneName, RecoilRelaxDuration);
        BeginBiomechanicalSettle();
    }

    /// <summary>
    /// World-space position of a ragdoll rigid body by bone name; null while the
    /// ragdoll is inactive or the bone has no body. Unlike reading the skeleton's
    /// ModelPose at framework time (which still reflects the animation pose at the
    /// death spot), this is where the physics actually put the body — cameras that
    /// track a kicked-around corpse must use it.
    /// </summary>
    public Vector3? GetBodyWorldPosition(string boneName)
    {
        if (simulation == null || !isActive) return null;
        var handle = FindBodyHandle(boneName);
        if (!handle.HasValue) return null;
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        var p = body.Pose.Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    // Temporarily free a joint so its child can swing: zero the angular motor force and widen the
    // swing-limit cone. Both are restored by TickRecoilRelaxers after the window.
    private void RelaxJointForRecoil(string boneName, float duration)
        => RelaxJoint(boneName, duration, widenSwing: true);

    /// <summary>
    /// Release a joint's motor on impact so the limb hanging off it carries its own momentum through.
    /// The swing limits are left ALONE — unlike a recoil, which is a limb being kicked and wants room to
    /// fly, a follow-through is a limb continuing under its own steam and must stay inside its anatomy.
    /// </summary>
    private void ReleaseJointForFollowThrough(string boneName, float duration)
        => RelaxJoint(boneName, duration, widenSwing: false);

    private void RelaxJoint(string boneName, float duration, bool widenSwing)
    {
        if (simulation == null) return;
        var relaxed = false;
        if (angularMotorByBone.TryGetValue(boneName, out var motor))
        {
            try
            {
                simulation.Solver.ApplyDescription(motor.Handle,
                    new AngularMotor { TargetVelocityLocalA = Vector3.Zero, Settings = new MotorSettings(0f, 0f) });
                relaxed = true;
            }
            catch { }
        }
        if (widenSwing && swingLimitByBone.TryGetValue(boneName, out var swing))
        {
            var wide = swing.Original;
            wide.MaximumSwingAngle = MathF.Max(swing.Original.MaximumSwingAngle, RecoilSwingAngle);
            try { simulation.Solver.ApplyDescription(swing.Handle, wide); relaxed = true; }
            catch { }
        }
        if (relaxed)
            recoilRelaxers.Add(new RecoilRelax { Bone = boneName, Remaining = duration });
    }

    private void TickRecoilRelaxers(float dt)
    {
        if (recoilRelaxers.Count == 0 || simulation == null) return;
        for (int i = recoilRelaxers.Count - 1; i >= 0; i--)
        {
            var r = recoilRelaxers[i];
            r.Remaining -= dt;
            if (r.Remaining > 0f) { recoilRelaxers[i] = r; continue; }
            recoilRelaxers.RemoveAt(i);
            // Restore the motor the joint was BUILT with, rather than re-inventing one. This used to put
            // an unlimited force back — so any joint the recoil had freed came back welded.
            if (angularMotorByBone.TryGetValue(r.Bone, out var motor))
                try { simulation.Solver.ApplyDescription(motor.Handle, motor.Original); }
                catch { /* constraint gone (sim torn down) */ }
            if (swingLimitByBone.TryGetValue(r.Bone, out var swing))
                try { simulation.Solver.ApplyDescription(swing.Handle, swing.Original); }
                catch { }
        }
    }

    /// <summary>
    /// Punt the ragdoll body nearest to <paramref name="from"/> within <paramref name="maxDist"/>.
    /// Uses the full set of ragdoll bodies (the body "point cloud") as the hit volume rather than a
    /// single named bone, so contact anywhere on the body registers. The impulse points away from
    /// <paramref name="from"/> horizontally plus an upward kick. Returns true if a body was hit.
    /// </summary>
    private bool PuntNearestCore(Vector3 from, float maxDist, float strength, float upBias = 0.5f)
    {
        if (simulation == null || !isActive) return false;

        BodyHandle? best = null;
        var bestDistSq = maxDist * maxDist;
        var bestPos = Vector3.Zero;
        foreach (var rb in ragdollBones)
        {
            var pos = simulation.Bodies.GetBodyReference(rb.BodyHandle).Pose.Position;
            var dSq = Vector3.DistanceSquared(pos, from);
            if (dSq <= bestDistSq) { bestDistSq = dSq; best = rb.BodyHandle; bestPos = pos; }
        }
        if (!best.HasValue) return false;

        var horiz = new Vector3(bestPos.X - from.X, 0f, bestPos.Z - from.Z);
        var dir = horiz.LengthSquared() > 0.0001f ? Vector3.Normalize(horiz) : Vector3.UnitX;
        var impulse = dir * strength + Vector3.UnitY * (strength * upBias);

        // A fully settled corpse is asleep. Setting velocity on a sleeping body before it is
        // activated doesn't stick, and a single woken body anchored to a sleeping constraint
        // island won't move. Wake the whole ragdoll first, THEN apply the impulse.
        WakeRagdollBodiesForBiomechanicalSettle();
        var body = simulation.Bodies.GetBodyReference(best.Value);
        body.Awake = true;
        body.Velocity.Linear += impulse;
        BeginBiomechanicalSettle();
        return true;
    }

    // Monster strike tuning: a collider bone must move faster than this to register a hit, and only
    // ragdoll bodies within this radius of the bone are hit.
    private const float StrikeMinSpeed = 1.5f;      // m/s
    private const float StrikeContactRadius = 0.6f; // m
    // A strike knocks the body AWAY from the contact point (outward + a little up), with strength
    // from the swing SPEED — using the raw swing velocity would drag the body along the arc (toward
    // the attacker on the retract phase).
    private const float StrikeUpBias = 0.3f;

    private Vector3 StrikeImpulse(Vector3 bodyPos, Vector3 contactPoint, float swingSpeed)
    {
        var away = new Vector3(bodyPos.X - contactPoint.X, 0f, bodyPos.Z - contactPoint.Z);
        var dir = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitX;
        return (dir + Vector3.UnitY * StrikeUpBias) * (swingSpeed * strikePower);
    }

    /// <summary>Impart a swing as an impulse to ragdoll bodies near <paramref name="point"/>.</summary>
    private void ApplyStrikeImpulse(Vector3 point, Vector3 swingVel, float radius)
    {
        if (simulation == null) return;
        if (standingActive) return;
        var speed = swingVel.Length();
        var radiusSq = radius * radius;
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var p = bodyRef.Pose.Position;
            if (Vector3.DistanceSquared(p, point) > radiusSq) continue;
            if (!struckThisWindow.Add(rb.BodyHandle.Value)) continue; // one hit per swing
            bodyRef.Velocity.Linear += StrikeImpulse(p, point, speed);
            bodyRef.Awake = true;
        }
    }

    private const float WeaponStrikeRadius = 0.55f; // generous around the blade line (weapons are thin)

    /// <summary>
    /// A humanoid's weapon is a separate draw object, so it isn't in the bone collision capsules.
    /// Treat the blade as a line segment (its world transform lives on the weapon's own skeleton root
    /// — DrawObject.Position alone doesn't move the mesh) and, during the attack window, impart its
    /// swing velocity to ragdoll bodies near the blade. No-op for unarmed/creature models.
    /// </summary>
    private void UpdateWeaponStrike(float dt)
    {
        if (simulation == null || strikeColliderAddress == nint.Zero) { weaponPrevValid = false; return; }
        if (!colliders.TryGetWeaponTransform(strikeColliderAddress, out var center, out var rot)) { weaponPrevValid = false; return; }
        if (rec != null) RecWeaponSample(center, rot);
        // Weapon-drop models the weapon as a Y-aligned capsule, so the blade runs along local Y.
        // Sample both ends so the blade direction's sign doesn't matter.
        var dir = Vector3.Transform(Vector3.UnitY, rot);
        var half = MathF.Max(0.4f, RagdollTuning.WeaponDropHalfLength);
        var a = center - dir * half;
        var b = center + dir * half;

        if (attackStrikeTimer > 0f && weaponPrevValid && dt > 0f)
        {
            var vel = ((a - prevBladeA) + (b - prevBladeB)) * (0.5f / dt);
            if (vel.LengthSquared() > StrikeMinSpeed * StrikeMinSpeed)
                ApplyStrikeImpulseSegment(a, b, vel, WeaponStrikeRadius);
        }
        prevBladeA = a;
        prevBladeB = b;
        weaponPrevValid = true;
    }

    /// <summary>Strike ragdoll bodies near the blade line segment a→b.</summary>
    private void ApplyStrikeImpulseSegment(Vector3 a, Vector3 b, Vector3 swingVel, float radius)
    {
        if (simulation == null) return;
        if (standingActive) return;
        var speed = swingVel.Length();
        var ab = b - a;
        var abLenSq = ab.LengthSquared();
        var radiusSq = radius * radius;
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var p = bodyRef.Pose.Position;
            var t = abLenSq > 1e-6f ? Math.Clamp(Vector3.Dot(p - a, ab) / abLenSq, 0f, 1f) : 0f;
            var closest = a + ab * t;
            if (Vector3.DistanceSquared(p, closest) > radiusSq) continue;
            if (!struckThisWindow.Add(rb.BodyHandle.Value)) continue; // one hit per swing
            bodyRef.Velocity.Linear += StrikeImpulse(p, closest, speed);
            bodyRef.Awake = true;
        }
    }

    /// <summary>Distance from <paramref name="from"/> to the nearest ragdoll body, or null if none.</summary>
    public float? NearestBodyDistance(Vector3 from)
    {
        if (simulation == null || !isActive) return null;
        float? best = null;
        foreach (var rb in ragdollBones)
        {
            var pos = simulation.Bodies.GetBodyReference(rb.BodyHandle).Pose.Position;
            var d = Vector3.Distance(pos, from);
            if (best == null || d < best.Value) best = d;
        }
        return best;
    }

    private void CancelAttackStrikeCore()
    {
        attackStrikeTimer = 0f;
        strikePower = 0f;
        strikeColliderAddresses.Clear();
        struckThisWindow.Clear();
        weaponPrevValid = false;
    }



    private void ApplyShakeCore(float intensity)
    {
        if (simulation == null || !isActive) return;
        var handle = FindBodyHandle(Bones.Pelvis);
        if (!handle.HasValue) return;
        var angle = (float)(random.NextDouble(RandomSite.ApplyShake) * MathF.PI * 2f);
        var impulse = new Vector3(MathF.Cos(angle) * intensity, 0f, MathF.Sin(angle) * intensity);
        var body = simulation.Bodies.GetBodyReference(handle.Value);
        body.Velocity.Linear += impulse;
        body.Awake = true;
        BeginBiomechanicalSettle();
    }
}
