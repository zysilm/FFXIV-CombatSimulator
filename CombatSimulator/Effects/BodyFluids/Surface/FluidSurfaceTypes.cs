// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System.Numerics;

namespace CombatSimulator.Effects.BodyFluids.Surface;

/// <summary>Valid only for the matching CharacterFluidSurface.Generation; never owns native pointers.</summary>
public struct FluidSurfaceAnchor
{
    public uint Generation;
    public int Triangle;
    public Vector3 Barycentric;
    public FluidSurfaceAnchor(uint generation, int triangle, Vector3 barycentric)
        => (Generation, Triangle, Barycentric) = (generation, triangle, barycentric);
}

public readonly record struct FluidSurfaceSample(Vector3 Position, Vector3 Normal, Vector3 Velocity);
