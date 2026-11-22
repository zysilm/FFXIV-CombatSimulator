// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera.DeathShots;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Camera;

/// <summary>
/// The world of the death shots and of the duel camera, answered with the game's collision: ground and walls by
/// <see cref="BGCollisionModule.RaycastMaterialFilter"/>, water by the water-material lookup. Identical queries
/// within one frame are answered once (to the millimetre: a coarser grid would step the eye's floor, which the
/// solver applies after its damping).
/// </summary>
internal sealed class CameraCollisionWorld : IDeathShotWorld
{
    private const float RayLength = 80f;
    /// <summary>A water surface is looked for up to here above the query and down to here below it. Not higher:
    /// a pool on a terrace above must not lift the eye onto it.</summary>
    private const float WaterSearchAbove = 4f, WaterSearchBelow = 20f;

    private readonly Dictionary<(int, int, int), float?> ground = new();
    private readonly Dictionary<(int, int, int), float?> water = new();
    private float subjectY = float.NaN;

    /// <summary>Rays cast this frame.</summary>
    public int Rays { get; private set; }

    public void NewFrame(float subjectHeight)
    {
        ground.Clear();
        water.Clear();
        Rays = 0;
        subjectY = subjectHeight;
    }

    private static (int, int, int) Key(Vector3 p) =>
        ((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f));

    /// <summary>The ground under the eye: from where the solver starts the ray (about 1.5 m above the eye), and,
    /// when that start is lower than the same lift above the body, from that height as well: a surface between
    /// the two is one the first ray started under (the eye is inside a slope or a bank at the body's level).</summary>
    public float? GroundBelow(Vector3 from)
    {
        var key = Key(from);
        if (ground.TryGetValue(key, out var known)) return known;
        var result = Down(from);
        if (float.IsFinite(subjectY))
        {
            var top = subjectY + DeathShotSolver.GroundRayLift;
            if (top > from.Y + 0.05f && Down(new Vector3(from.X, top, from.Z)) is { } higher && higher > from.Y)
                result = higher;
        }
        ground[key] = result;
        return result;
    }

    private float? Down(Vector3 from)
    {
        try
        {
            Rays++;
            if (BGCollisionModule.RaycastMaterialFilter(from, new Vector3(0f, -1f, 0f), out var hit, RayLength) &&
                float.IsFinite(hit.Point.Y))
                return hit.Point.Y;
        }
        catch
        {
            // Collision module unavailable (zone transition): no ground.
        }
        return null;
    }

    public float? WaterBelow(Vector3 from)
    {
        var key = Key(from);
        if (water.TryGetValue(key, out var known)) return known;
        float? result = null;
        try
        {
            Rays++;
            if (WorldCollisionMesh.TryFindWaterSurface(from, WaterSearchAbove, WaterSearchBelow, out var surface))
                result = surface;
        }
        catch
        {
        }
        water[key] = result;
        return result;
    }

    public bool Blocked(Vector3 from, Vector3 to, out float distance) => Blocked(from, to, out distance, out _);

    /// <summary>With the normal of what stopped the ray (the below-ground shots tell the ground from a wall by it).</summary>
    public bool Blocked(Vector3 from, Vector3 to, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = default;
        var along = to - from;
        var length = along.Length();
        if (!(length > 1e-3f)) return false;
        try
        {
            Rays++;
            if (!BGCollisionModule.RaycastMaterialFilter(from, along / length, out var hit, length))
                return false;
            distance = Vector3.Distance(hit.Point, from);
            var n = hit.Normal;
            if (float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z)) normal = n;
            return float.IsFinite(distance) && distance < length;
        }
        catch
        {
            return false;
        }
    }
}
