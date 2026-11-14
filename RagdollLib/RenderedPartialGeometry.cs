// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Numerics;

namespace CombatSimulator.Animation;

/// <summary>Connection-aligned partial-skeleton skin matrices. Both input arrays are
/// MODEL-space poses. Align the reference and animated connections independently; this
/// accepts partials expressed locally or already aligned to the character model.</summary>
internal static class RenderedPartialGeometry
{
    internal static bool TrySkinMatrices(Matrix4x4[] reference, Matrix4x4[] current, int connected,
        Matrix4x4 primaryReferenceConnection, Matrix4x4 primaryCurrentConnection, out Matrix4x4[] skin)
    {
        skin = Array.Empty<Matrix4x4>();
        if (reference.Length == 0 || reference.Length != current.Length || (uint)connected >= reference.Length ||
            !Matrix4x4.Invert(reference[connected], out var inverseReferenceConnection) ||
            !Matrix4x4.Invert(current[connected], out var inverseCurrentConnection)) return false;
        var referenceAttachment = inverseReferenceConnection * primaryReferenceConnection;
        var currentAttachment = inverseCurrentConnection * primaryCurrentConnection;
        skin = new Matrix4x4[reference.Length];
        for (int i = 0; i < skin.Length; i++)
        {
            if (!Matrix4x4.Invert(reference[i] * referenceAttachment, out var inverseReference)) return false;
            skin[i] = inverseReference * current[i] * currentAttachment;
            if (!float.IsFinite(skin[i].GetDeterminant())) return false;
        }
        return true;
    }
}
