// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Numerics;
using System.Runtime.InteropServices;

namespace CombatSimulator.Rendering.WorldGeometry;

/// <summary>A triangle-list vertex. All geometry and normals use world space.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WorldVertex
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector4 Color;

    public WorldVertex(Vector3 position, Vector4 color) : this(position, Vector3.UnitY, color) { }
    public WorldVertex(Vector3 position, Vector3 normal, Vector4 color)
    {
        Position = position;
        Normal = normal;
        Color = color;
    }
}
