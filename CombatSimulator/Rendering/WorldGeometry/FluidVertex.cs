// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Numerics;
using System.Runtime.InteropServices;
namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>
/// World-space surface. Thin producers supply a local normal-thickness estimate;
/// explicitly declared closed ellipsoids derive their interior path analytically.
/// Thickness never includes the gap to scene depth. Legacy curved caps are approximate.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FluidVertex
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 UV;
    public readonly float Thickness;
    public readonly float Coverage;
    /// <summary>Positive radii explicitly identify a closed ellipsoid; zero means the supplied thin surface only.</summary>
    public readonly Vector3 VolumeCenter;
    public readonly Vector3 VolumeRadii;
    public FluidVertex(Vector3 position, Vector3 normal, Vector2 uv, float thickness, float coverage = 1,
        Vector3 volumeCenter = default, Vector3 volumeRadii = default)
    {
        Position = position; Normal = normal; UV = uv; Thickness = thickness; Coverage = coverage;
        VolumeCenter = volumeCenter; VolumeRadii = volumeRadii;
    }
}

/// <summary>Immutable optical parameters. Reflection currently uses an explicit studio fallback.</summary>
public readonly record struct FluidMaterial(float IndexOfRefraction, float Roughness, Vector3 Absorption,
    float RefractionStrength, float ReflectionStrength, FluidDiagnosticView DiagnosticView = FluidDiagnosticView.Composite)
{
    public static FluidMaterial Default => new(1.333f, 0.08f, new Vector3(0.2f, 0.08f, 0.03f), 1f, 1f);
    internal bool IsValid => float.IsFinite(IndexOfRefraction) && IndexOfRefraction >= 1 && IndexOfRefraction <= 2.5f
        && float.IsFinite(Roughness) && Roughness >= 0.02f && Roughness <= 1
        && WorldGeometryBuilder.Finite(Absorption) && Absorption.X >= 0 && Absorption.Y >= 0 && Absorption.Z >= 0
        && float.IsFinite(RefractionStrength) && RefractionStrength >= 0 && RefractionStrength <= 4
        && float.IsFinite(ReflectionStrength) && ReflectionStrength >= 0 && ReflectionStrength <= 4
        && (uint)DiagnosticView <= (uint)FluidDiagnosticView.RefractionOffsetPixels;
}

/// <summary>Production optical views for distinguishing background capture from normal/material faults.</summary>
public enum FluidDiagnosticView
{
    Composite = 0,
    RefractedBackgroundOnly = 1,
    NormalFacing = 2,
    DepthDecision = 3,
    SignedNormalFacing = 4,
    RefractionPathReason = 5,
    RefractionOffsetPixels = 6,
}
