// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using CombatSimulator.Animation.CollapseProfiles;
using CombatSimulator.Animation.Hosting;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace CombatSimulator.Animation;

/// <summary>
/// The plugin's ragdoll: the library's <see cref="Ragdoll"/> (AdvancedRagdoll) on the FFXIV host. Stage A4 moved the
/// ragdoll into the library; this thin shell keeps what touches the game and every name the plugin uses: the
/// constructors that build the FFXIV host (<see cref="FfxivRagdollHost"/>) and subscribe the render hook and the
/// framework tick, the guided-collapse profiles embedded in the plugin, the FFXIV humanoid profile, and the members
/// whose signatures carry game types or FFXIV bone names. Everything else (including the nested types, reachable as
/// <c>RagdollController.X</c>) is inherited unchanged.
/// </summary>
public unsafe class RagdollController : Ragdoll
{
    // Only the render-hook subscription uses it; null offline.
    private readonly BoneTransformService? boneService;

    /// <summary>
    /// Registers the FFXIV humanoid profile with the library when the plugin assembly loads, before any plugin code
    /// can create a ragdoll or read <see cref="Ragdoll.AllBoneDefaults"/> (the tables were static initializers of this
    /// class until stage A4).
    /// </summary>
#pragma warning disable CA2255 // The plugin is loaded as a library, but this is exactly a module-load registration.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterHumanoidProfile() => UseHumanoidProfile(FfxivHumanoidProfile.Profile);

    public RagdollController(BoneTransformService boneService, Npcs.NpcSelector npcSelector,
        Safety.MovementBlockHook movementBlockHook, Configuration config, IPluginLog log,
        Func<IReadOnlyList<nint>>? extraCollisionProvider = null)
        : this(FfxivRagdollHost.Create(boneService, config, log, npcSelector, movementBlockHook, extraCollisionProvider), boneService)
    {
        boneService.OnRenderFrame += OnRenderFrame;
        Core.Services.Framework.Update += ApplyDeferredFollowTransform;
    }

    /// <summary>Lightweight constructor for NPC ragdolls (no NPC collision volumes or movement blocking).</summary>
    public RagdollController(BoneTransformService boneService, Configuration config, IPluginLog log)
        : this(FfxivRagdollHost.Create(boneService, config, log, null, null, null), boneService)
    {
        boneService.OnRenderFrame += OnRenderFrame;
        Core.Services.Framework.Update += ApplyDeferredFollowTransform;
    }

    /// <summary>
    /// A controller on the given host services. The caller drives it: <see cref="Ragdoll.OnRenderFrame"/> once per
    /// rendered frame (the plugin subscribes it to the render hook) and the public methods. Offline
    /// (<paramref name="boneService"/> null) nothing is subscribed.
    /// </summary>
    internal RagdollController(RagdollHost host, BoneTransformService? boneService = null)
        : base(host, new CollapseProfileBook(host.Log))
    {
        this.boneService = boneService;
    }

    private void ApplyDeferredFollowTransform(IFramework _) => OnFrameworkTick();

    public override void Dispose()
    {
        base.Dispose();
        if (boneService != null)
        {
            boneService.OnRenderFrame -= OnRenderFrame;
            Core.Services.Framework.Update -= ApplyDeferredFollowTransform;
        }
    }

    /// <summary>The validated model parser, which stage A2b moved to the mesh source (FfxivMeshSource). Kept here
    /// under its old name for callers outside the ragdoll that still use it.</summary>
    internal static bool TryReadModelGeometryData(byte[] bytes, out ModelGeometryData data)
        => FfxivMeshSource.TryReadModelGeometryData(bytes, out data);

    /// <summary>A dropped weapon's collision hulls from the model it is drawn with, or null when mesh collision for
    /// weapons is off or the model is unsupported.</summary>
    public WeaponMeshGeometry? TryBuildWeaponMesh(DrawObject* draw) => TryBuildWeaponMesh((nint)draw);

    /// <inheritdoc cref="Ragdoll.CreateStandingSupport"/>
    public new bool CreateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = FfxivHumanoidProfile.PelvisBoneName)
        => base.CreateStandingSupport(anchorWorldPos, uprightRot, anchorBoneName);

    /// <inheritdoc cref="Ragdoll.UpdateStandingSupport"/>
    public new bool UpdateStandingSupport(Vector3 anchorWorldPos, Quaternion uprightRot, string anchorBoneName = FfxivHumanoidProfile.PelvisBoneName)
        => base.UpdateStandingSupport(anchorWorldPos, uprightRot, anchorBoneName);
}
