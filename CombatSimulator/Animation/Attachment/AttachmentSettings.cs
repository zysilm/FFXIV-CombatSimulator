// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation.Attachment;

public enum GarmentTemplate { Top, Coat, Dress, Trousers, Skirt, Rigid }
public enum GarmentMaterial { Fabric, Silk, HeavyWeave, Leather, Armor }

public sealed class AttachmentOffset
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public Vector3 ToVector() => new(X, Y, Z);
}

/// <summary>Saved independently of Visual only. Distances are in metres on a scale-one actor.</summary>
public sealed class AttachmentSettings
{
    public GarmentTemplate Template { get; set; }
    public GarmentMaterial Material { get; set; }
    public float SlipDistance { get; set; } = 0.35f;
    public float SpeedLimit { get; set; } = 0.6f;
    public float Firmness { get; set; } = 0.75f;
    public float BodyFriction { get; set; } = 0.45f;
    public float GroundFriction { get; set; } = 0.7f;
    public float Damping { get; set; } = 1.5f;
    public float SwayDistance { get; set; } = 0.008f;
    public float LateralDistance { get; set; } = 0.025f;
    public float SkirtSwingDegrees { get; set; } = 65f;
    public float SkirtStiffness { get; set; } = 24f;
    public float SkirtDamping { get; set; } = 1.5f;
    public float Thickness { get; set; } = 0.012f;
    public float OpeningScale { get; set; } = 1.12f;
    public float Asymmetry { get; set; } = 0.15f;
    public bool SelfCollision { get; set; } = true;
    public bool LayerCollision { get; set; } = true;
    // Per-bone attachment range multiplier. Zero pins the opening, one uses SlipDistance.
    // This edits the permanent connections, not an instruction to break them after a timer.
    public Dictionary<string, float> Anchors { get; set; } = new();
    public Dictionary<string, AttachmentOffset> AnchorOffsets { get; set; } = new();

    public AttachmentSettings Copy()
    {
        var copy = (AttachmentSettings)MemberwiseClone();
        copy.Anchors = Anchors == null ? new() : new(Anchors);
        copy.AnchorOffsets = new();
        if (AnchorOffsets != null)
            foreach (var (key, offset) in AnchorOffsets)
                if (offset != null) copy.AnchorOffsets[key] = new AttachmentOffset { X = offset.X, Y = offset.Y, Z = offset.Z };
        return copy;
    }

    public AttachmentSettings Validated()
    {
        var copy = Copy();
        if (!Enum.IsDefined(copy.Template)) copy.Template = GarmentTemplate.Top;
        if (!Enum.IsDefined(copy.Material)) copy.Material = GarmentMaterial.Fabric;
        copy.SlipDistance = FiniteClamp(SlipDistance, 0f, 1.5f, 0.35f);
        copy.SpeedLimit = FiniteClamp(SpeedLimit, 0.05f, 4f, 0.6f);
        copy.Firmness = FiniteClamp(Firmness, 0f, 1f, 0.75f);
        copy.BodyFriction = FiniteClamp(BodyFriction, 0f, 2f, 0.45f);
        copy.GroundFriction = FiniteClamp(GroundFriction, 0f, 2f, 0.7f);
        copy.Damping = FiniteClamp(Damping, 0f, 12f, 1.5f);
        copy.SwayDistance = FiniteClamp(SwayDistance, 0f, 0.02f, 0.008f);
        copy.LateralDistance = FiniteClamp(LateralDistance, 0f, 0.1f, 0.025f);
        copy.SkirtSwingDegrees = FiniteClamp(SkirtSwingDegrees, 0, 110, 65);
        copy.SkirtStiffness = FiniteClamp(SkirtStiffness, 4, 100, 24);
        copy.SkirtDamping = FiniteClamp(SkirtDamping, 1, 4, 1.5f);
        copy.Thickness = FiniteClamp(Thickness, 0.003f, 0.04f, 0.012f);
        copy.OpeningScale = FiniteClamp(OpeningScale, 1f, 1.8f, 1.12f);
        copy.Asymmetry = FiniteClamp(Asymmetry, 0f, 0.6f, 0.15f);
        foreach (var name in new List<string>(copy.Anchors.Keys))
            copy.Anchors[name] = FiniteClamp(copy.Anchors[name], 0f, 2f, 1f);
        foreach (var offset in copy.AnchorOffsets.Values)
        {
            offset.X = FiniteClamp(offset.X, -0.3f, 0.3f, 0);
            offset.Y = FiniteClamp(offset.Y, -0.3f, 0.3f, 0);
            offset.Z = FiniteClamp(offset.Z, -0.3f, 0.3f, 0);
        }
        return copy;
    }

    public void ApplyMaterial(GarmentMaterial material)
    {
        Material = material;
        (SkirtStiffness, SkirtDamping) = material switch
        {
            GarmentMaterial.Silk => (8f, 1.2f),
            GarmentMaterial.HeavyWeave => (40f, 2f),
            GarmentMaterial.Leather => (60f, 2f),
            GarmentMaterial.Armor => (100f, 3f),
            _ => (24f, 1.5f),
        };
        (BodyFriction, GroundFriction, Damping) = material switch
        {
            GarmentMaterial.Silk => (0.16f, 0.3f, 0.7f),
            GarmentMaterial.HeavyWeave => (0.65f, 0.9f, 2.8f),
            GarmentMaterial.Leather => (0.6f, 0.8f, 3f),
            GarmentMaterial.Armor => (0.35f, 0.65f, 2.2f),
            _ => (0.45f, 0.7f, 1.5f),
        };
    }

    public (float Mass, float Stretch, float Bend) Mechanics => Template == GarmentTemplate.Rigid
        ? (0.12f, 0.0000002f, 0.0000005f) : Material switch
    {
        GarmentMaterial.Silk => (0.012f, 0.00003f, 0.008f),
        GarmentMaterial.HeavyWeave => (0.06f, 0.000008f, 0.0008f),
        GarmentMaterial.Leather => (0.045f, 0.000004f, 0.00015f),
        GarmentMaterial.Armor => (0.12f, 0.0000002f, 0.0000005f),
        _ => (0.025f, 0.00002f, 0.003f),
    };

    private static float FiniteClamp(float value, float min, float max, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
