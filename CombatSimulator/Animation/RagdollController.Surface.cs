// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation.SurfaceProfiles;
using CombatSimulator.Animation.Hosting;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>The resolved race/body profile used by this corpse, if it has a supported humanoid identity.</summary>
    public string? ActiveCharacterSurfaceProfileName => characterSurfaceRuntime?.Profile.Name;

    /// <summary>Returns the lightweight profile surface nearest to a point on one named ragdoll bone.</summary>
    public bool TryGetCharacterSurface(
        string boneName,
        Vector3 worldPoint,
        CharacterSurfaceUsage usage,
        out CharacterSurfaceHit hit)
    {
        hit = default;
        return characterSurfaceRuntime != null &&
               characterSurfaceRuntime.TryGetNearestSurface(boneName, worldPoint, usage, out hit);
    }

    /// <summary>Returns the lightweight profile surface nearest to a point on the whole ragdoll.</summary>
    public bool TryGetCharacterSurface(
        Vector3 worldPoint,
        CharacterSurfaceUsage usage,
        float maxDistance,
        out CharacterSurfaceHit hit)
    {
        hit = default;
        return characterSurfaceRuntime != null &&
               characterSurfaceRuntime.TryGetNearestSurface(worldPoint, usage, maxDistance, out hit);
    }

    /// <summary>World-space profile triangles for developer visualization.</summary>
    public IEnumerable<CharacterSurfaceDebugBone> GetCharacterSurfaceDebugBones(CharacterSurfaceUsage usage,
        int maximumFacesPerBone = 0, int maximumFacesTotal = 0)
        => characterSurfaceRuntime?.GetDebugBones(usage, maximumFacesPerBone, maximumFacesTotal) ?? Array.Empty<CharacterSurfaceDebugBone>();

    private CharacterSurfaceIdentity ReadCharacterSurfaceIdentity()
        => ReadCharacterSurfaceIdentity(targetCharacterAddress);

    private CharacterSurfaceIdentity ReadCharacterSurfaceIdentity(nint characterAddress)
        => ReadCharacterSurfaceIdentity(actors, characterAddress);

    /// <summary>The surface identity (race, body type, ...) of a character, read through the host.</summary>
    internal static CharacterSurfaceIdentity ReadCharacterSurfaceIdentity(IRagdollActor actors, nint characterAddress)
    {
        if (characterAddress == nint.Zero)
            return default;

        Span<byte> customize = stackalloc byte[0x1A];
        actors.ReadAppearance(characterAddress, customize, out var modelCharaId);
        return new CharacterSurfaceIdentity(
            customize[0x00], // race
            customize[0x01], // gender
            customize[0x04], // tribe
            customize[0x02], // body type
            customize[0x03], // height
            customize[0x15], // bust / muscle tone
            modelCharaId);
    }

    private void BuildCharacterSurfaceRuntime(bool genericSkeleton)
    {
        characterSurfaceRuntime = null;
        if (genericSkeleton || simulation == null || ragdollBones.Count == 0)
            return;

        var identity = ReadCharacterSurfaceIdentity();
        if (!identity.IsHumanoid)
            return;

        // The runtime surface is a lightweight query copy of the collision proxy that was
        // actually accepted. Do not re-apply the old JSON race profile here: doing so would make
        // traversal/grab/debug disagree with physics even after the mesh measurement was valid.
        var profile = new CharacterSurfaceProfile
        {
            Id = "mesh-collision-runtime",
            Name = "Mesh-Derived Collision Proxy",
            Gender = byte.MaxValue,
            MinHeightScale = 1f,
            MaxHeightScale = 1f,
            Margins = new CharacterSurfaceMargins
            {
                Physics = 1f,
                Traversal = 1f,
                Grab = 1f,
                Ground = 1f,
            },
            DefaultScale = new CharacterSurfaceRoleScales
            {
                Width = 1f,
                Depth = 1f,
                Length = 1f,
                EndOverlap = 0f,
                TaperStart = 1f,
                TaperMiddle = 1f,
                TaperEnd = 1f,
            },
        };

        var seeds = new CharacterSurfaceBoneSeed[ragdollBones.Count];
        for (var i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            var radiusX = rb.ColliderShape == RagdollColliderShape.Box
                ? rb.BoxHalfExtents.X
                : rb.CapsuleRadius;
            var radiusZ = rb.ColliderShape == RagdollColliderShape.Box
                ? rb.BoxHalfExtents.Z
                : rb.CapsuleRadius;
            var halfLength = rb.ColliderShape == RagdollColliderShape.Box
                ? rb.BoxHalfExtents.Y
                : rb.CapsuleHalfLength;
            var role = activeDefByName.TryGetValue(rb.Name, out var def)
                ? def.AnatomicalRole.ToString()
                : AnatomicalRole.Generic.ToString();
            seeds[i] = new CharacterSurfaceBoneSeed(
                rb.Name, role, halfLength, radiusX, radiusZ,
                rb.ColliderShape == RagdollColliderShape.Box,
                rb.MeshGeometry);
        }

        characterSurfaceRuntime = new CharacterSurfaceRuntime(profile, identity, seeds);
        UpdateCharacterSurfaceRuntime();
        log.Info($"RagdollController: final collision-proxy surface cached for {ragdollBones.Count} bones.");
    }

    private void UpdateCharacterSurfaceRuntime()
    {
        if (characterSurfaceRuntime == null || simulation == null)
            return;

        foreach (var rb in ragdollBones)
        {
            var body = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            characterSurfaceRuntime.UpdateBonePose(
                rb.Name, body.Pose.Position, body.Pose.Orientation);
        }
    }
}
