// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.Hair;

internal readonly record struct HairMeshInfluence(string Bone, float Weight);

internal readonly record struct HairMeshSample(Vector3 Position, HairMeshInfluence[] Influences)
{
    // Dominant weights classify samples; every influence still contributes to
    // their posed location. In particular, face-side cards often blend scalp
    // and hair bones and cannot be fitted as rigidly attached to one bone.
    public bool TryPose(IReadOnlyDictionary<string, Matrix4x4> skin, out Vector3 position)
    {
        position = default;
        float total = 0;
        foreach (var influence in Influences)
        {
            if (!(influence.Weight > 0) || !float.IsFinite(influence.Weight) ||
                !skin.TryGetValue(influence.Bone, out var transform)) return false;
            position += Vector3.Transform(Position, transform) * influence.Weight;
            total += influence.Weight;
        }
        if (total < .001f) return false;
        position /= total;
        return FlexibleHairSolver.Finite(position);
    }
}
