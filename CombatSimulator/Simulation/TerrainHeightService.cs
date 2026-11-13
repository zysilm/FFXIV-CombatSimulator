// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CombatSimulator.Simulation;

/// <summary>The real ground under a point, by a short downward ray into the map's collision.</summary>
public static class TerrainHeightService
{
    /// <summary>
    /// The ground under a point, by one ray from <paramref name="above"/> over it down to
    /// <paramref name="below"/> under it: short, so it finds the floor a body stands on and not the
    /// roof over a room.
    /// </summary>
    public static bool TrySampleGroundNear(Vector3 point, float above, float below, out float y)
    {
        y = 0f;
        if (!BGCollisionModule.RaycastMaterialFilter(
                point + new Vector3(0f, above, 0f),
                new Vector3(0, -1, 0),
                out var hit,
                above + below))
            return false;
        y = hit.Point.Y;
        return float.IsFinite(y);
    }

    /// <summary>One downward ray for a single actor (the fighting lane). Starts a little above
    /// <paramref name="nearY"/> so a ceiling far overhead is not mistaken for the floor.</summary>
    public static bool TrySampleGround(float x, float z, float nearY, out float y)
    {
        y = 0f;
        if (!BGCollisionModule.RaycastMaterialFilter(
                new Vector3(x, nearY + 3f, z),
                new Vector3(0, -1, 0),
                out var hit,
                12f))
            return false;

        y = hit.Point.Y;
        return true;
    }
}
