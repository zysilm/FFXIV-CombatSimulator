// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Composition;

/// <summary>
/// The hook safety checker and the experimental dev module's seams (docs/PLUGIN_ARCHITECTURE.md §6.1).
/// The dev module itself is created by the plugin's constructor (its <c>#if DEV_EXPERIMENTAL</c> block)
/// and handed here; the plugin class keeps its partial-method extension points.
/// </summary>
internal sealed unsafe class SafetyDevModule
{
    private readonly PluginModules m;

    private HookSafetyChecker hookSafetyChecker = null!;
    private Dev.IDevExperimental devExperimental = null!;

    public SafetyDevModule(PluginModules modules) => m = modules;

    public HookSafetyChecker HookSafety => hookSafetyChecker;
    public Dev.IDevExperimental DevExperimental => devExperimental;

    private IPluginLog log => m.Log;
    private AnimationController animationController => m.World.Animation;
    private CombatEngine combatEngine => m.Combat.Engine;
    private NpcActionProfileProvider npcActionProfileProvider => m.Actors.ActionProfiles;
    private NpcSpawner npcSpawner => m.Actors.Spawner;

    /// <summary>
    /// The remaining experimental ("easter-egg") dev features live in one module (Victory/Hold +
    /// dev-only per-frame tweaks). The engine drives the cinematic victory through the
    /// IVictorySequence seam.
    /// </summary>
    public void WireDevExperimental(Dev.IDevExperimental module)
    {
        devExperimental = module;
        combatEngine.VictorySequence = devExperimental.VictorySequence;
        combatEngine.ShouldSuppressEnemyInitiation = () => devExperimental.SuppressEnemyInitiation;
        combatEngine.OnPlayerAttackLanded = devExperimental.OnPlayerAttackLanded;
        combatEngine.PlayerAttackReplacement = devExperimental.TryReplacePlayerAttack;
        npcActionProfileProvider.ForceAutoAttackOnly = () => devExperimental.NpcAutoAttackOnly;
        npcSpawner.StripBodyLegs = () => devExperimental.VirtualEnemyStripBodyLegs;
        npcSpawner.StripAccessories = () => devExperimental.VirtualEnemyStripAccessories;
    }

    /// <summary>
    /// Hook safety checker — register native functions we CALL (not hook) that other plugins may hook.
    /// We check for JMP detours at each address to detect third-party hooks.
    /// </summary>
    public void CreateHookSafetyChecker(IDalamudPluginInterface pluginInterface)
    {
        hookSafetyChecker = new HookSafetyChecker(pluginInterface, log);
        // expectedFirstByte from each function's signature pattern (fallback if JMP pattern is unrecognized)
        hookSafetyChecker.Register("ActorVfxCreate",
            "Spawns VFX on actors. Hooked by VFXEditor, RotationSolver.",
            animationController.ActorVfxCreateAddress, expectedFirstByte: 0x40); // sig: "40 53 55 56 57..."
        hookSafetyChecker.Register("ActorVfxRemove",
            "Removes VFX from actors. Hooked by VFX-related plugins.",
            animationController.ActorVfxRemoveAddress); // resolved via pointer chase — no expected byte
        hookSafetyChecker.Register("ActionEffectHandler.Receive",
            "Processes combat effects (damage, flytext, animations). Hooked by ACT, combat log plugins.",
            (nint)FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler.MemberFunctionPointers.Receive);
    }
}
