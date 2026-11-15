// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Battle.Rules;

/// <summary>
/// Where an ally kneels at a body (raising a fallen friend, praying over the lord): right beside it, so the
/// kneeling is seen at the body. The spot is <see cref="Offset"/> from the body's centre toward where the
/// kneeler came from, on the walkable floor, never more than <see cref="MaxFromBody"/> from the body, and
/// clear of where anyone else kneels. Only where to kneel: when a raise or a prayer counts is the rules'
/// own reach, unchanged. Pure: the floor is asked of the caller (the navigation mesh in the plugin, the test
/// mesh in Tests/Navigation).
/// </summary>
public static class KneelSpot
{
    /// <summary>From the body's centre toward where the kneeler came from: beside it, not inside it.</summary>
    public const float Offset = 0.25f;

    /// <summary>The spot is never further than this (flat) from the body.</summary>
    public const float MaxFromBody = 0.3f;

    /// <summary>Two kneelers' spots are at least this far apart (flat): they do not overlap.</summary>
    public const float Clearance = 0.8f;

    /// <summary>
    /// A spot is at least this far (flat) from any other fallen body nearby (one who knelt here before and fell
    /// here): the next kneels on the other side, not on top of it.
    /// </summary>
    public const float CorpseClearance = 0.45f;

    // A fallen body this close to the one knelt at is that body itself.
    private const float SameBody = 0.05f;

    /// <summary>One that has not got to its spot this long after setting off for it kneels where it is, as before.</summary>
    public const float StallSeconds = 2f;

    /// <summary>The body has moved this much further than <see cref="MaxFromBody"/> from the spot: a new spot is chosen.</summary>
    public const float BodyDrift = 0.3f;

    /// <summary>Whether the body has moved away from the spot chosen beside it.</summary>
    public static bool BodyMovedAway(Vector3 spot, Vector3 body) => Flat(spot, body) > MaxFromBody + BodyDrift;

    // The approach first, then round the body either way, the far side last.
    private static readonly float[] Turns = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };

    /// <summary>
    /// The walkable floor nearest a point within <paramref name="within"/> metres sideways (its height the
    /// real ground there); false when there is none.
    /// </summary>
    public delegate bool FloorNear(Vector3 point, float within, out Vector3 floor);

    /// <summary>
    /// The spot to kneel at beside <paramref name="body"/> for someone coming from <paramref name="from"/>:
    /// toward them first, else turned round the body, the first one on the floor within
    /// <see cref="MaxFromBody"/> of the body and <see cref="Clearance"/> from every spot in
    /// <paramref name="taken"/>; of those, the first <see cref="CorpseClearance"/> from every other body in
    /// <paramref name="corpses"/>, else (all crowded by the fallen) the one furthest from them. False when
    /// there is none (the caller kneels where it is, as before).
    /// </summary>
    public static bool TryChoose(Vector3 body, Vector3 from, IReadOnlyList<Vector3> taken, IReadOnlyList<Vector3> corpses,
        FloorNear floorNear, out Vector3 spot)
    {
        spot = body;
        var toward = new Vector2(from.X - body.X, from.Z - body.Z);
        var heading = toward.LengthSquared() > 1e-6f ? MathF.Atan2(toward.X, toward.Y) : 0f;
        var found = false;
        var bestRoom = -1f;
        foreach (var turn in Turns)
        {
            var angle = heading + turn * (MathF.PI / 180f);
            var candidate = body + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * Offset;
            if (!floorNear(candidate, MaxFromBody, out var floor) || Flat(floor, body) > MaxFromBody)
                continue;
            var clear = true;
            foreach (var other in taken)
            {
                if (Flat(floor, other) < Clearance)
                {
                    clear = false;
                    break;
                }
            }
            if (!clear)
                continue;
            // Room from the other fallen: enough, and this is the spot; else remember the roomiest.
            var room = float.MaxValue;
            foreach (var corpse in corpses)
                if (Flat(corpse, body) > SameBody)
                    room = MathF.Min(room, Flat(floor, corpse));
            if (room >= CorpseClearance)
            {
                spot = floor;
                return true;
            }
            if (room > bestRoom)
            {
                bestRoom = room;
                spot = floor;
                found = true;
            }
        }
        return found;
    }

    /// <summary>Horizontal distance.</summary>
    public static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
