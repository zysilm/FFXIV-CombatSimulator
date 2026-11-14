// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation.Hosting;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Animation;

/// <summary>
/// Plugin-scoped weapon drop physics: the library's <see cref="WeaponDropSimulation"/> (AdvancedRagdoll) on the FFXIV
/// host. Stage A5 moved the simulation into the library; this thin shell keeps the constructor the plugin uses, builds
/// the host (<see cref="FfxivWeaponDropHost"/>) and subscribes the render hook. Every public member
/// (<c>SpawnFor</c>, <c>RemoveFor</c>, <c>RemoveAll</c>, <c>HasEntry</c>, <c>AcquireExternalControl</c>, <c>Dispose</c>)
/// is inherited unchanged.
/// </summary>
public class WeaponDropController : WeaponDropSimulation
{
    private readonly BoneTransformService boneService;

    public WeaponDropController(BoneTransformService boneService, Configuration config, IPluginLog log, RagdollController ragdoll)
        : base(FfxivWeaponDropHost.Create(boneService, config, log), ragdoll)
    {
        this.boneService = boneService;
        boneService.OnRenderFrame += OnRenderFrame;
    }

    public override void Dispose()
    {
        boneService.OnRenderFrame -= OnRenderFrame;
        base.Dispose();
    }
}
