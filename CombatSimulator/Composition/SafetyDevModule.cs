// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.Havok.Animation.Rig;

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
    /// Hook safety checker — register native functions we CALL (not hook) with our simulated actors or in
    /// simulation paths, that other plugins plausibly hook: their detour then runs inside our call. Our own
    /// hooks need no registration; the checker finds them in Dalamud's hook registry.
    /// Only the first three raise the conflict warning: there we hand the game input it never produces
    /// itself (fabricated effect packets, manually spawned VFX). The rest are the game's ordinary spawn /
    /// appearance / draw / pose functions, which it calls for every actor; Penumbra and Glamourer hook
    /// several of them on almost every install, so they are shown for crash diagnosis only.
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
            (nint)ActionEffectHandler.MemberFunctionPointers.Receive);

        // Spawning and dressing simulated actors (enemies, allies, spectators).
        hookSafetyChecker.Register("CreateBattleCharacter",
            "Creates our simulated actors. Hooked by actor-spawning and object-tracking tools.",
            (nint)ClientObjectManager.MemberFunctionPointers.CreateBattleCharacter, raisesConflict: false);
        hookSafetyChecker.Register("DeleteObjectByIndex",
            "Removes our simulated actors. Hooked by actor-spawning and object-tracking tools.",
            (nint)ClientObjectManager.MemberFunctionPointers.DeleteObjectByIndex, raisesConflict: false);
        hookSafetyChecker.Register("CopyFromCharacter",
            "Copies appearance onto our actors. Hooked by Penumbra (collection inheritance), Brio-style tools.",
            (nint)CharacterSetupContainer.MemberFunctionPointers.CopyFromCharacter, raisesConflict: false);
        hookSafetyChecker.Register("LoadWeapon",
            "Loads our actors' weapons. Hooked by Penumbra, Glamourer.",
            (nint)DrawDataContainer.MemberFunctionPointers.LoadWeapon, raisesConflict: false);
        hookSafetyChecker.Register("LoadEquipment",
            "Changes our actors' gear. Hooked by Glamourer-style appearance plugins.",
            (nint)DrawDataContainer.MemberFunctionPointers.LoadEquipment, raisesConflict: false);
        // Virtual: the address is the entry in a live BattleChara's vtable (our actors are BattleChara,
        // as is the local player), resolved at each scan.
        hookSafetyChecker.Register("EnableDraw",
            "Makes our spawned actors visible. Hooked by Penumbra (assigns mod collections).",
            ResolveBattleCharaEnableDraw, raisesConflict: false);

        // Animation, sound, ragdoll.
        hookSafetyChecker.Register("PlayActionTimeline",
            "Plays animations on our actors. Hooked by animation / emote / posing tools.",
            (nint)TimelineContainer.MemberFunctionPointers.PlayActionTimeline, raisesConflict: false);
        hookSafetyChecker.Register("SoundManager.PlaySound",
            "Plays enemy voices. Hooked by sound filter / replacement plugins.",
            (nint)SoundManager.MemberFunctionPointers.PlaySound, raisesConflict: false);
        hookSafetyChecker.Register("hkaPose.SyncModelSpace",
            "Syncs ragdoll bone poses. Hooked by posing tools (Ktisis, Brio).",
            (nint)hkaPose.MemberFunctionPointers.SyncModelSpace, raisesConflict: false);
        hookSafetyChecker.Register("Human.ResolveMdlPath",
            "Reads model paths when fitting the ragdoll to a body. Hooked by Penumbra.",
            ResolveHumanResolveMdlPath, raisesConflict: false);
    }

    private static nint ResolveBattleCharaEnableDraw()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0) return 0;
        var obj = (GameObject*)player.Address;
        return obj->VirtualTable == null ? 0 : (nint)obj->VirtualTable->EnableDraw;
    }

    // Our call goes to the vtable of the actor's draw object; Human is the one Penumbra always hooks
    // (monster/demihuman draw objects have their own entries, not checked).
    private static nint ResolveHumanResolveMdlPath()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0) return 0;
        var draw = ((GameObject*)player.Address)->DrawObject;
        if (draw == null || draw->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase) return 0;
        var cb = (CharacterBase*)draw;
        if (cb->GetModelType() != CharacterBase.ModelType.Human || cb->VirtualTable == null) return 0;
        return (nint)cb->VirtualTable->ResolveMdlPath;
    }
}
