using System;
using System.Numerics;
using CombatSimulator.Animation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Fighting;

/// <summary>
/// Resolves the player's main-hand weapon as a world-space line segment (grip → tip)
/// for weapon-contact hit detection. Primary path reads the weapon's OWN draw-object
/// skeleton (world root transform + bone-AABB length, the same data WeaponDropController
/// uses for its drop box); fallback for unarmed / unloaded weapons is the hand attach
/// bone plus a configured length along the character's facing.
/// </summary>
public unsafe sealed class WeaponHitboxService
{
    public readonly record struct WeaponSegment(Vector3 Base, Vector3 Tip);

    private static readonly string[] HandBones = { "n_buki_r", "j_te_r" };

    private readonly Configuration config;
    private readonly BoneTransformService boneService;
    private readonly IPluginLog log;

    public WeaponHitboxService(Configuration config, BoneTransformService boneService, IPluginLog log)
    {
        this.config = config;
        this.boneService = boneService;
        this.log = log;
    }

    public WeaponSegment? GetMainHandSegment(nint characterAddress)
    {
        if (characterAddress == nint.Zero)
            return null;

        try
        {
            var character = (Character*)characterAddress;
            var weaponDraw = character->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawObject;
            if (weaponDraw != null)
            {
                var seg = SegmentFromWeaponSkeleton(weaponDraw, characterAddress);
                if (seg.HasValue)
                    return seg;
            }

            return SegmentFromHandBone(characterAddress);
        }
        catch
        {
            return null;
        }
    }

    private WeaponSegment? SegmentFromWeaponSkeleton(DrawObject* weaponDraw, nint ownerAddress)
    {
        var skelN = boneService.TryGetSkeletonFromCharBase((CharacterBase*)weaponDraw);
        if (skelN == null)
            return null;
        var skel = skelN.Value;

        var wSkeleton = skel.CharBase->Skeleton;
        if (wSkeleton == null)
            return null;

        var rootPos = new Vector3(
            wSkeleton->Transform.Position.X,
            wSkeleton->Transform.Position.Y,
            wSkeleton->Transform.Position.Z);
        var rootRot = new Quaternion(
            wSkeleton->Transform.Rotation.X,
            wSkeleton->Transform.Rotation.Y,
            wSkeleton->Transform.Rotation.Z,
            wSkeleton->Transform.Rotation.W);
        var scale = weaponDraw->Scale.X <= 0f ? 1f : weaponDraw->Scale.X;

        if (skel.BoneCount >= 2)
        {
            // Bone AABB in model space; the longest cardinal axis is the blade line.
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var i = 0; i < skel.BoneCount; i++)
            {
                ref var b = ref skel.Pose->ModelPose.Data[i];
                var p = new Vector3(b.Translation.X, b.Translation.Y, b.Translation.Z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var span = max - min;
            var longest = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
            if (longest >= 0.05f)
            {
                var center = (min + max) * 0.5f;
                Vector3 endA, endB;
                if (span.X >= span.Y && span.X >= span.Z)
                {
                    endA = center with { X = min.X };
                    endB = center with { X = max.X };
                }
                else if (span.Y >= span.Z)
                {
                    endA = center with { Y = min.Y };
                    endB = center with { Y = max.Y };
                }
                else
                {
                    endA = center with { Z = min.Z };
                    endB = center with { Z = max.Z };
                }

                var worldA = rootPos + Vector3.Transform(endA * scale, rootRot);
                var worldB = rootPos + Vector3.Transform(endB * scale, rootRot);

                // Grip = the end nearer the owner's hand; bones rarely reach the mesh
                // tip, so pad the length a little (config-scaled, clamped sane).
                var handPos = GetHandPosition(ownerAddress) ?? rootPos;
                var (grip, far) = Vector3.DistanceSquared(worldA, handPos) <= Vector3.DistanceSquared(worldB, handPos)
                    ? (worldA, worldB)
                    : (worldB, worldA);

                var dir = far - grip;
                var rawLen = dir.Length();
                if (rawLen > 0.01f)
                {
                    dir /= rawLen;
                    var len = Math.Clamp(rawLen * MathF.Max(0.5f, config.FightingModeWeaponLengthScale), 0.4f, 3.0f);
                    return new WeaponSegment(grip, grip + dir * len);
                }
            }
        }

        // Single-bone (or degenerate) weapon: configured local axis and length off the root.
        var axis = config.FightingModeWeaponAxis switch
        {
            0 => Vector3.UnitX,
            2 => Vector3.UnitZ,
            _ => Vector3.UnitY,
        };
        var worldDir = Vector3.Transform(axis, rootRot);
        if (worldDir.LengthSquared() < 0.0001f)
            return null;
        worldDir = Vector3.Normalize(worldDir);
        var length = Math.Clamp(config.FightingModeWeaponLength, 0.2f, 3.0f);
        return new WeaponSegment(rootPos, rootPos + worldDir * length);
    }

    private WeaponSegment? SegmentFromHandBone(nint characterAddress)
    {
        Vector3? handPos = GetHandPosition(characterAddress);
        if (!handPos.HasValue)
            return null;

        var obj = (GameObject*)characterAddress;
        var yaw = obj->Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var length = Math.Clamp(config.FightingModeWeaponLength, 0.2f, 3.0f);
        return new WeaponSegment(handPos.Value, handPos.Value + forward * length);
    }

    private Vector3? GetHandPosition(nint characterAddress)
    {
        foreach (var bone in HandBones)
        {
            var p = boneService.GetBoneWorldPos(characterAddress, bone);
            if (p.HasValue)
                return p;
        }
        return null;
    }

    /// <summary>Closest distance between two segments (weapon sweep vs hurtbox axis).</summary>
    public static float SegmentSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);

        float s, t;
        if (a <= 1e-6f && e <= 1e-6f)
            return r.Length();
        if (a <= 1e-6f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-6f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > 1e-6f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        var c1 = p1 + d1 * s;
        var c2 = p2 + d2 * t;
        return Vector3.Distance(c1, c2);
    }
}
