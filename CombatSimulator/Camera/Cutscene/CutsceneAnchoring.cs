// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Camera.Cutscene;

/// <summary>
/// One of our actors as a cutscene attach target, read once per frame by the plugin (or made up by the offline test):
/// where it stands and faces, whether it is down (dead or ragdolled), its race's bind points, and a live bone lookup.
/// </summary>
public sealed class CutsceneActor
{
    public string Label { get; init; } = "";
    /// <summary>Root (feet) position, world.</summary>
    public Vector3 Position { get; init; }
    /// <summary>Facing, radians; the actor faces (sin yaw, 0, cos yaw) — its local +Z, as in the cutscene placements.</summary>
    public float Yaw { get; init; }
    /// <summary>Dead or ragdolled: a body on the ground. Root-height bind points are then remapped to live bones.</summary>
    public bool Down { get; init; }
    /// <summary>Multiplies root-height offsets; 1 for a human (its race's .eid already carries its height), the
    /// measured head height over the reference race's for anything else.</summary>
    public float HeightScale { get; init; } = 1f;
    /// <summary>The race's bind points (the reference race for a non-human).</summary>
    public IReadOnlyDictionary<int, BindPoint>? BindPoints { get; init; }
    /// <summary>World position (and rotation, when it can be trusted) of a bone by name; null when it has none.</summary>
    public Func<string, (Vector3 Position, Quaternion? Rotation)?> Bone { get; init; } = _ => null;
    /// <summary>The floor under the actor (for the eye clamp).</summary>
    public float FloorY { get; init; }
}

/// <summary>
/// Attach slots resolved on our actors (docs/CUTSCENE_SHOTS.md): the bind point's world frame is a translation to the
/// point and the actor's facing (offsets in the actor's own frame), as the curated list was measured.
/// <list type="bullet">
/// <item>Bone points (head 1/26/27, chest 28, belly 18, weapons 77/78, ...): the live bone (the ragdoll body when the
/// actor is ragdolled) plus the .eid offset in the bone's frame when its rotation is known.</item>
/// <item>Root-height points (bone n_root: 76 face height, 42 above the head, 71 the root, ...): standing, the actor's
/// position plus the offset turned by its facing; down, the live head (76, 42 and other points from 1.3 m up) or
/// pelvis (71 and the lower ones), so the camera does not stare at the air where a standing person's face would be.</item>
/// </list>
/// Game-free.
/// </summary>
public static class CutsceneAnchoring
{
    public const string HeadBone = "j_kao";
    public const string PelvisBone = "j_kosi";
    /// <summary>Root-height points at or above this offset are a head when the body is down.</summary>
    public const float HeadHeightThreshold = 1.3f;

    public static Matrix4x4 Facing(float yaw) => Matrix4x4.CreateRotationY(yaw);

    /// <summary>The world frame of a bind point on an actor; <paramref name="note"/> says what was remapped or missing.</summary>
    public static Matrix4x4 Frame(CutsceneActor actor, int point, out string? note)
    {
        note = null;
        var facing = Facing(actor.Yaw);
        if (actor.BindPoints == null || !actor.BindPoints.TryGetValue(point, out var bp))
        {
            note = $"pt{point}: no bind point → root";
            return facing * Matrix4x4.CreateTranslation(actor.Position);
        }

        Vector3 pos;
        if (bp.Bone == "n_root")
        {
            var offset = bp.Offset * actor.HeightScale;
            if (!actor.Down)
                pos = actor.Position + Vector3.Transform(offset, facing);
            else
            {
                var head = point is 76 or 42 || bp.Offset.Y >= HeadHeightThreshold;
                var boneName = point == 71 ? PelvisBone : head ? HeadBone : PelvisBone;
                if (actor.Bone(boneName) is { } b)
                {
                    pos = b.Position;
                    note = $"pt{point} (n_root+{bp.Offset.Y:F2}) → {boneName} (body down)";
                }
                else
                {
                    pos = actor.Position + Vector3.Transform(offset, facing);
                    note = $"pt{point}: body down but no {boneName} → root offset";
                }
            }
        }
        else if (actor.Bone(bp.Bone) is { } b)
        {
            pos = b.Position + (b.Rotation is { } r ? Vector3.Transform(bp.Offset, r) : Vector3.Zero);
        }
        else
        {
            // A bone the actor does not have (a monster without j_kao, say): a plausible height on its root.
            var h = bp.Bone switch { HeadBone => 1.55f, "j_sebo_c" => 1.3f, "j_kubi" => 1.45f, _ => 1.0f } * actor.HeightScale;
            pos = actor.Down ? (actor.Bone(PelvisBone)?.Position ?? actor.Position) : actor.Position + new Vector3(0, h, 0);
            note = $"pt{point}: no bone {bp.Bone} → {(actor.Down ? "pelvis" : $"root+{h:F2}")}";
        }
        return facing * Matrix4x4.CreateTranslation(pos);
    }
}
