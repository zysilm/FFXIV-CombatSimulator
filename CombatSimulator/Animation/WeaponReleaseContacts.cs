using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;

namespace CombatSimulator.Animation;

// Only pairs belonging to a newly released weapon and its owner's hands/forearms enter
// this set. Once separated, a pair stays enabled, including subsequent falls onto the arm.
internal sealed class WeaponReleaseContacts
{
    private readonly Dictionary<(int Weapon, int Arm), int> waiting = new();
    public void Add(int weapon, int arm) => waiting[(weapon, arm)] = 0;
    public bool Allows(int a, int b) => !waiting.ContainsKey((a, b)) && !waiting.ContainsKey((b, a));
    public void Observe(int weapon, int arm, bool separated)
    {
        var key = (weapon, arm);
        if (!waiting.TryGetValue(key, out var frames)) return;
        if (!separated) waiting[key] = 0;
        else if (frames >= 1) waiting.Remove(key);
        else waiting[key] = frames + 1;
    }
    public void Remove(int handle)
    {
        foreach (var key in new List<(int Weapon, int Arm)>(waiting.Keys))
            if (key.Weapon == handle || key.Arm == handle) waiting.Remove(key);
    }

    public static bool IsReleaseArm(string? parent, string? child)
        => parent != null && (parent.StartsWith("j_ude_b_", StringComparison.Ordinal) ||
            parent.StartsWith("j_te_", StringComparison.Ordinal)) ||
            child != null && child.StartsWith("j_te_", StringComparison.Ordinal);

    // Conservative separation: the capsule's complete rotational sweep is contained in
    // a sphere swept between the previous and target centres. No timer can expire inside it.
    public static bool Separated(RigidPose weapon, Vector3 half, Vector3 armStart, Vector3 armEnd, float armReach)
    {
        var extent = Vector3.Abs(Vector3.Transform(Vector3.UnitX, weapon.Orientation)) * half.X
                   + Vector3.Abs(Vector3.Transform(Vector3.UnitY, weapon.Orientation)) * half.Y
                   + Vector3.Abs(Vector3.Transform(Vector3.UnitZ, weapon.Orientation)) * half.Z;
        var armMin = Vector3.Min(armStart, armEnd) - new Vector3(armReach + .02f);
        var armMax = Vector3.Max(armStart, armEnd) + new Vector3(armReach + .02f);
        var min = weapon.Position - extent;
        var max = weapon.Position + extent;
        return min.X > armMax.X || max.X < armMin.X || min.Y > armMax.Y || max.Y < armMin.Y ||
               min.Z > armMax.Z || max.Z < armMin.Z;
    }

    public static void MoveKinematic(BodyReference body, Vector3 target, Quaternion rotation, float dt)
    {
        rotation = Quaternion.Normalize(rotation);
        var deltaPosition = target - body.Pose.Position;
        if (deltaPosition.LengthSquared() > 1f)
        {
            // Parking/teleporting must not manufacture a launch velocity.
            body.Pose = new RigidPose(target, rotation);
            body.Velocity = default;
            body.Awake = true;
            return;
        }
        var delta = Quaternion.Normalize(rotation * Quaternion.Inverse(body.Pose.Orientation));
        if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        var xyz = new Vector3(delta.X, delta.Y, delta.Z);
        var sin = xyz.Length();
        body.Velocity.Linear = deltaPosition / dt;
        body.Velocity.Angular = sin < 1e-6f ? Vector3.Zero : xyz * (2 * MathF.Atan2(sin, delta.W) / (sin * dt));
        // Keep the previous pose. BEPU integrates these velocities to the target exactly once.
        if (body.Velocity.Linear.LengthSquared() + body.Velocity.Angular.LengthSquared() > 1e-10f)
            body.Awake = true;
    }
}
