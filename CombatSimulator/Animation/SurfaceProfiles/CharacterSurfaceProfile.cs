// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.SurfaceProfiles;

public enum CharacterSurfaceUsage
{
    Physics,
    Traversal,
    Grab,
    Ground,
}

public readonly record struct CharacterSurfaceIdentity(
    byte Race,
    byte Gender,
    byte Tribe,
    byte BodyType,
    byte Height,
    byte BustOrTone,
    int ModelCharaId)
{
    public bool IsHumanoid => Race is >= 1 and <= 8 && ModelCharaId == 0;
    public float HeightRatio => Math.Clamp(Height / 100f, 0f, 1f);
    public float BustOrToneRatio => Math.Clamp(BustOrTone / 100f, 0f, 1f);
}

public sealed class CharacterSurfaceProfileSet
{
    public int Version { get; set; } = 1;
    public List<CharacterSurfaceProfile> Profiles { get; set; } = new();
}

public sealed class CharacterSurfaceProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public byte Race { get; set; }
    public byte Gender { get; set; }
    public byte Tribe { get; set; }
    public byte BodyType { get; set; }
    public int ModelCharaId { get; set; }
    public float MinHeightScale { get; set; } = 0.96f;
    public float MaxHeightScale { get; set; } = 1.04f;
    public CharacterSurfaceMargins Margins { get; set; } = new();
    public CharacterSurfaceRoleScales DefaultScale { get; set; } = new();
    public Dictionary<string, CharacterSurfaceRoleScales> RoleScales { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CharacterSurfaceBoneOverride> Bones { get; set; }
        = new(StringComparer.Ordinal);

    public bool Matches(CharacterSurfaceIdentity identity)
    {
        if (ModelCharaId != 0 && ModelCharaId != identity.ModelCharaId)
            return false;
        if (Race != 0 && Race != identity.Race)
            return false;
        if (Gender != byte.MaxValue && Gender != identity.Gender)
            return false;
        if (Tribe != 0 && Tribe != identity.Tribe)
            return false;
        return BodyType == 0 || BodyType == identity.BodyType;
    }

    public int Specificity(CharacterSurfaceIdentity identity)
    {
        if (!Matches(identity)) return -1;
        var score = 0;
        if (ModelCharaId != 0) score += 32;
        if (Race != 0) score += 16;
        if (Gender != byte.MaxValue) score += 8;
        if (Tribe != 0) score += 4;
        if (BodyType != 0) score += 2;
        return score;
    }

    public float ResolveHeightScale(CharacterSurfaceIdentity identity)
        => MinHeightScale + (MaxHeightScale - MinHeightScale) * identity.HeightRatio;

    public CharacterSurfaceRoleScales ResolveRoleScale(string role)
    {
        if (!string.IsNullOrEmpty(role) && RoleScales.TryGetValue(role, out var scale))
            return scale;
        return DefaultScale;
    }

    public float UsageScale(CharacterSurfaceUsage usage) => usage switch
    {
        CharacterSurfaceUsage.Physics => Margins.Physics,
        CharacterSurfaceUsage.Traversal => Margins.Traversal,
        CharacterSurfaceUsage.Grab => Margins.Grab,
        CharacterSurfaceUsage.Ground => Margins.Ground,
        _ => 1f,
    };
}

public sealed class CharacterSurfaceMargins
{
    public float Physics { get; set; } = 0.98f;
    public float Traversal { get; set; } = 0.90f;
    public float Grab { get; set; } = 1f;
    public float Ground { get; set; } = 0.96f;
}

public sealed class CharacterSurfaceRoleScales
{
    public float Width { get; set; } = 1f;
    public float Depth { get; set; } = 1f;
    public float Length { get; set; } = 1f;
    public float OffsetX { get; set; }
    public float OffsetZ { get; set; }
    public float EndOverlap { get; set; } = 0.04f;
    public float TaperStart { get; set; } = 1f;
    public float TaperMiddle { get; set; } = 1f;
    public float TaperEnd { get; set; } = 1f;
}

public sealed class CharacterSurfaceBoneOverride
{
    public string Role { get; set; } = string.Empty;
    public List<CharacterSurfaceRing> Rings { get; set; } = new();
}

public readonly record struct CharacterSurfaceRing(
    float T,
    float OffsetX,
    float OffsetZ,
    float RadiusX,
    float RadiusZ);

public readonly record struct CharacterSurfaceHit(
    string BoneName,
    Vector3 Point,
    Vector3 Normal,
    Vector3 BodyLocalPoint,
    float DistanceSquared);
