// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Core;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Composition;

/// <summary>
/// Bodies: the player's ragdoll and every NPC's (queued, staggered, removed), dropped weapons, severed
/// parts, armour coming off, arrows that stuck, the player's weapon visibility; and the questions the
/// fight asks about bodies (<see cref="ICorpseQuery"/>) (docs/PLUGIN_ARCHITECTURE.md §6.1, §6.5).
/// </summary>
internal sealed unsafe class CorpseModule : ICorpseQuery
{
    private const int NpcRagdollActivationsPerFrame = 1;
    // Extra activation delay handed to each NPC ragdoll in a burst, and the ceiling on it, so the
    // last body of a big wave still hits the floor while the fight reads as over. See
    // ActivateNpcDeathRagdoll.
    private const float NpcRagdollActivationStagger = 0.2f;
    private const float NpcRagdollMaxStagger = 1.2f;
    private float npcRagdollStaggerBudget;

    private readonly PluginModules m;

    private RagdollController ragdollController = null!;
    private WeaponDropController weaponDropController = null!;
    private Effects.PlayerWeaponVisibilityController playerWeaponVisibilityController = null!;
    private Effects.ArrowImpactController arrowImpactController = null!;
    private DismembermentController dismembermentController = null!;
    private Dev.KoStripController armorDetachmentController = null!;

    // NPC ragdoll controllers (multiple concurrent, persist until sim stop/reset/zone change)
    private readonly Dictionary<nint, RagdollController> npcRagdolls = new();
    private readonly Queue<nint> pendingNpcRagdolls = new();
    private readonly HashSet<nint> pendingNpcRagdollAddresses = new();

    public CorpseModule(PluginModules modules) => m = modules;

    public RagdollController PlayerRagdoll => ragdollController;
    public WeaponDropController WeaponDrop => weaponDropController;
    public Effects.PlayerWeaponVisibilityController PlayerWeaponVisibility => playerWeaponVisibilityController;
    public Effects.ArrowImpactController ArrowImpacts => arrowImpactController;
    public DismembermentController Dismemberment => dismembermentController;
    public Dev.KoStripController ArmorDetachment => armorDetachmentController;
    /// <summary>The NPC ragdolls kept, by character address (the FrameCost line counts them).</summary>
    public IReadOnlyDictionary<nint, RagdollController> NpcRagdolls => npcRagdolls;

    private ICorpseModuleSettings config => m.Config;
    private IPluginLog log => m.Log;
    private BoneTransformService boneTransformService => m.World.BoneTransforms;
    private MovementBlockHook movementBlockHook => m.World.MovementBlock;
    private AnimationController animationController => m.World.Animation;
    private GlamourerIpc glamourerIpc => m.World.Glamourer;
    private Rendering.WorldGeometry.WorldGeometryRenderer worldGeometryRenderer => m.World.GeometryRenderer;
    private NpcSelector npcSelector => m.Actors.Selector;
    private CombatCompanionManager companionManager => m.Actors.Companions;
    private CombatEngine combatEngine => m.Combat.Engine;
    private Dev.IDevExperimental devExperimental => m.SafetyDev.DevExperimental;
    private SimulatedNpc? FindNpcByAddress(nint address) => m.Actors.FindNpcByAddress(address);

    /// <summary>The player's ragdoll, the weapon drop and the player's weapon visibility.</summary>
    public void CreatePlayerBody(IObjectTable objectTable)
    {
        ragdollController = new RagdollController(boneTransformService, npcSelector, movementBlockHook, m.Config, log, GetPartyCollisionAddresses);
        ragdollController.RunAnimatedHair = true;
        weaponDropController = new WeaponDropController(boneTransformService, m.Config, log, ragdollController);
        playerWeaponVisibilityController = new Effects.PlayerWeaponVisibilityController(m.Config, objectTable, boneTransformService);
    }

    public void CreateArrowImpacts()
    {
        arrowImpactController = new Effects.ArrowImpactController(m.Config, boneTransformService, worldGeometryRenderer, log);
        animationController.ArrowImpacts = arrowImpactController;
    }

    /// <summary>Ragdoll recording, severed parts and armour coming off.</summary>
    public void CreateLimbsAndArmor(IDalamudPluginInterface pluginInterface, IObjectTable objectTable)
    {
        RagdollRecordingSession.Configure(System.IO.Path.Combine(pluginInterface.GetPluginConfigDirectory(), "ragdoll-rec"), new Animation.Hosting.DalamudRagdollLog(log));
        dismembermentController = new DismembermentController(boneTransformService, glamourerIpc, animationController, objectTable, m.Config, log);
        dismembermentController.PlayerRagdollController = ragdollController;
        // Clothes still on the corpse ride along when it is picked up; the dismemberment side owns the
        // question of which garments are still being worn.
        ragdollController.CarryExternalRigOnAnchor = dismembermentController.ShouldCarryExternalRig;
        dismembermentController.EnemyNpcIdentityResolver = address =>
        {
            var npc = FindNpcByAddress(address);
            if (npc != null) return (npc.BNpcBaseId, npc.BNpcNameId);
            // Untracked live enemy: read its real BNpc ids straight off the actor so the severed
            // piece is rebuilt via SetupBNpc (full appearance, incl. equipment) instead of a bare
            // ModelCharaId — the latter omits the configured equipment.
            if (objectTable.CreateObjectReference(address) is Dalamud.Game.ClientState.Objects.Types.IBattleNpc bnpc)
                return (bnpc.BaseId, bnpc.NameId);
            return (0u, 0u);
        };
        // Spin the body-side stump at the cut so a kicked-off piece makes the stump visibly flick.
        // The body is whichever ragdoll drives that address (an NPC's own, else the player/default).
        dismembermentController.ReactionRecoilSink = (addr, bone, angularVel) =>
        {
            var body = npcRagdolls.TryGetValue(addr, out var found) ? found : ragdollController;
            body?.ApplyAngularVelocity(bone, angularVel);
        };
        armorDetachmentController = new Dev.KoStripController(m.Config, glamourerIpc, dismembermentController, log);
    }

    public void OnNpcDeathRagdoll(nint address)
    {
        // Fighting camera death transition must run regardless of ragdoll settings.

        if (address == nint.Zero)
            return;

        // Ahead of the ragdoll gates below: dev death reactions are independent of them.
        devExperimental.OnNpcDeath(address);

        if (!config.EnableRagdoll || !config.EnableNpcDeathRagdoll)
            return;

        // The enemy's death ragdoll: its armour comes off with the player's settings ("Enemies" ticked,
        // humanoids wearing equipment only).
        armorDetachmentController.StripOtherOnKo(address, ally: false);

        if (HasLiveNpcRagdoll(address) || pendingNpcRagdollAddresses.Contains(address))
            return;

        pendingNpcRagdolls.Enqueue(address);
        pendingNpcRagdollAddresses.Add(address);
    }

    public void ProcessPendingNpcRagdolls(float deltaTime)
    {
        // The stagger budget bleeds off in real time whether or not anything is queued, so a burst
        // fans out and a quiet stretch returns it to zero.
        npcRagdollStaggerBudget = Math.Max(0f, npcRagdollStaggerBudget - deltaTime);

        if (pendingNpcRagdolls.Count == 0)
            return;

        if (!config.EnableRagdoll || !config.EnableNpcDeathRagdoll || !combatEngine.IsActive)
        {
            ClearPendingNpcRagdolls();
            return;
        }

        for (var i = 0; i < NpcRagdollActivationsPerFrame && pendingNpcRagdolls.Count > 0; i++)
        {
            var address = pendingNpcRagdolls.Dequeue();
            pendingNpcRagdollAddresses.Remove(address);
            ActivateNpcDeathRagdoll(address);
        }
    }

    private void ActivateNpcDeathRagdoll(nint address)
    {
        if (address == nint.Zero || !config.EnableRagdoll || !config.EnableNpcDeathRagdoll)
            return;

        // The weapon is released at once; only the body waits for the ragdoll activation delay.
        weaponDropController.SpawnFor(address);

        if (HasLiveNpcRagdoll(address)) return;

        log.Info($"NPC death ragdoll: activating for 0x{address:X}");

        var partyMode = config.EnableCombatCompanions;
        var controller = partyMode
            ? new RagdollController(boneTransformService, npcSelector, movementBlockHook, m.Config, log, GetPartyCollisionAddresses)
            : new RagdollController(boneTransformService, m.Config, log);

        IReadOnlyList<string> bones = Array.Empty<string>();
        if (config.EnableEnemyDismemberment)
        {
            bones = dismembermentController.SelectRandomEnemyBones(
                address,
                config.EnemyHumanoidDismembermentCount,
                config.EnemyMonsterDismembermentBonePercent);
        }

        controller.SetDismemberedBones(bones);
        if (bones.Count > 0)
        {
            foreach (var bone in bones)
                dismembermentController.SpawnFor(address, bone, AdvancedRagdoll.RagdollTuning.NpcActivationDelay, null);
            log.Info($"NPC death ragdoll: selected {bones.Count} visual parts for 0x{address:X}");
        }

        // Fan the settle windows apart. Construction is already spread one-per-frame
        // (NpcRagdollActivationsPerFrame), but that is the cheap half — what costs is the second or
        // two each rig spends awake before it reaches the resting fast path, and a wave that wipes
        // together lands all of those on top of each other. Each activation pushes the next one
        // further out; the budget decays in ProcessPendingNpcRagdolls, so it only bites during a
        // burst and is back to zero by the next fight.
        controller.Activate(address, AdvancedRagdoll.RagdollTuning.NpcActivationDelay + npcRagdollStaggerBudget);
        npcRagdollStaggerBudget = MathF.Min(
            npcRagdollStaggerBudget + NpcRagdollActivationStagger, NpcRagdollMaxStagger);
        npcRagdolls[address] = controller;
    }

    private void ClearPendingNpcRagdolls()
    {
        pendingNpcRagdolls.Clear();
        pendingNpcRagdollAddresses.Clear();
    }

    private bool HasLiveNpcRagdoll(nint address)
    {
        if (!npcRagdolls.TryGetValue(address, out var controller))
            return false;

        var stale = !controller.IsActive || controller.TargetCharacterAddress != address;
        if (!stale)
        {
            var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
            stale = obj == null ||
                    (controller.TargetEntityId != 0 && obj->EntityId != 0 && obj->EntityId != controller.TargetEntityId);
        }

        if (!stale)
            return true;

        controller.Dispose();
        npcRagdolls.Remove(address);
        log.Info($"NPC death ragdoll: removed stale entry for 0x{address:X}");
        return false;
    }

    public void OnCompanionDeathRagdoll(nint address)
    {
        if (!config.EnableRagdoll || !config.PartyCompanionDeathRagdoll) return;

        // The ally's death ragdoll: its armour comes off with the player's settings ("Allies" ticked).
        armorDetachmentController.StripOtherOnKo(address, ally: true);

        // The weapon is released at once; only the body waits for the ragdoll activation delay.
        weaponDropController.SpawnFor(address);

        if (npcRagdolls.ContainsKey(address)) return;

        log.Info($"Companion death ragdoll: activating for 0x{address:X}");

        var controller = new RagdollController(boneTransformService, npcSelector, movementBlockHook, m.Config, log, GetPartyCollisionAddresses);
        controller.SetDismemberedBones(Array.Empty<string>());
        // Shares the enemy stagger budget — a wipe drops companions alongside enemies, and they are
        // the same kind of load. See ActivateNpcDeathRagdoll.
        // An executed captive drops at once, from the kneel.
        var executed = false;
        foreach (var companion in companionManager.Companions)
            if (companion.Address == address)
                executed = companion.State.Executed;
        if (executed)
            controller.Activate(address, 0f, AdvancedRagdoll.RagdollTriggerMode.FixedDelay);
        else
        {
            controller.Activate(address, config.RagdollActivationDelay,
                AdvancedRagdoll.RagdollTrigger.ClampMode(config.RagdollTriggerMode), npcRagdollStaggerBudget);
            npcRagdollStaggerBudget = MathF.Min(
                npcRagdollStaggerBudget + NpcRagdollActivationStagger, NpcRagdollMaxStagger);
        }
        npcRagdolls[address] = controller;
    }

    /// <summary>
    /// A hit landed (<c>CombatEvents.DamageLanded</c>): an ally or an enemy struck by the other side loses
    /// its next piece of armour, like the player on each enemy hit ("Allies" / "Enemies" ticked).
    /// </summary>
    public void NoteArmorHit(SimulatedEntityState target, DamageResult hit, SimulatedEntityState? source)
    {
        if (target.IsPlayer || (!config.KoStripAllies && !config.KoStripEnemies) || !config.KoStripOnHitEnabled)
            return;

        if (target.IsCompanion)
        {
            foreach (var companion in companionManager.Companions)
                if (ReferenceEquals(companion.State, target) && companion.IsSpawned && companion.Address != nint.Zero)
                {
                    armorDetachmentController.TryStripNextOnHitOther(companion.Address, ally: true);
                    return;
                }
            return;
        }

        foreach (var npcs in new IEnumerable<SimulatedNpc>[] { npcSelector.SelectedNpcs, m.Actors.Spawner.SpawnedNpcs })
            foreach (var npc in npcs)
                if (ReferenceEquals(npc.State, target) && npc.BattleChara != null)
                {
                    armorDetachmentController.TryStripNextOnHitOther(npc.Address, ally: false);
                    return;
                }
    }

    /// <summary>
    /// Collision actors for a party-mode ragdoll: the local player plus every spawned
    /// companion. Enemies are supplied separately via the NpcSelector, and each ragdoll
    /// skips its own body, so this makes every ragdoll collide with every living
    /// character (party↔party, party↔enemy, enemy↔party, and the player).
    /// </summary>
    private IReadOnlyList<nint> GetPartyCollisionAddresses()
    {
        var list = new List<nint>();

        var player = Services.ObjectTable.LocalPlayer;
        if (player != null && player.Address != nint.Zero)
        {
            list.Add(player.Address);
            if (config.NpcCollisionActive)
            {
                AddMountCollisionAddresses(list);
            }
        }

        foreach (var companion in companionManager.Companions)
        {
            if (companion.IsSpawned && companion.Address != nint.Zero)
                list.Add(companion.Address);
        }

        return list;
    }

    /// <summary>
    /// Return the highest low corpse surface under an NPC's next foot position. High/flying body
    /// parts are deliberately rejected: this is a small step-over aid, not arbitrary climbing.
    /// </summary>
    public float? ResolveCorpseTraversalHeight(nint actorAddress, Vector3 position)
    {
        if (!config.NpcCollisionActive)
            return null;

        var footProbeRadius = 0.24f;
        if (actorAddress != nint.Zero)
        {
            var gameObject = (GameObject*)actorAddress;
            if (gameObject->DrawObject != null)
            {
                var scale = gameObject->DrawObject->Scale;
                var maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                if (float.IsFinite(maxScale) && maxScale > 0f)
                    footProbeRadius *= MathF.Max(1f, maxScale);
            }
        }
        float? best = null;

        void Consider(RagdollController controller)
        {
            if (!controller.TryGetWalkableSurfaceHeight(position, footProbeRadius, out var surfaceY))
                return;
            if (!best.HasValue || surfaceY > best.Value)
                best = surfaceY;
        }

        Consider(ragdollController);
        foreach (var controller in npcRagdolls.Values)
            Consider(controller);

        return best;
    }

    public IReadOnlyList<RagdollController> GetActiveCorpseRagdolls()
    {
        var result = new List<RagdollController>(npcRagdolls.Count + 1);
        if (ragdollController.IsActive)
            result.Add(ragdollController);
        foreach (var controller in npcRagdolls.Values)
            if (controller.IsActive)
                result.Add(controller);
        return result;
    }

    private void AddMountCollisionAddresses(List<nint> list)
    {
        foreach (var obj in Services.ObjectTable)
        {
            if (obj == null || obj.Address == nint.Zero)
                continue;
            if (obj.ObjectKind != ObjectKind.Mount)
                continue;

            list.Add(obj.Address);
        }
    }

    public void RemoveNpcRagdoll(nint address)
    {
        if (npcRagdolls.TryGetValue(address, out var controller))
        {
            controller.Dispose();
            npcRagdolls.Remove(address);
        }
    }

    public void DeactivateAllNpcRagdolls()
    {
        ClearPendingNpcRagdolls();
        foreach (var controller in npcRagdolls.Values)
            controller.Dispose();
        npcRagdolls.Clear();
    }

    /// <summary>Where a fallen actor's body lies: its ragdoll's pelvis, or null without one.</summary>
    public Vector3? BodyPositionOf(nint address)
    {
        if (!config.EnableRagdoll || address == nint.Zero)
            return null;
        if (npcRagdolls.TryGetValue(address, out var npc))
            return npc.IsActive ? npc.GetBodyWorldPosition("j_kosi") : null;
        return Core.Services.ObjectTable.LocalPlayer?.Address == address ? LordBodyPosition() : null;
    }

    /// <summary>Where the lord's body lies: its ragdoll pelvis once it falls, else where the lord died.</summary>
    public Vector3? LordBodyPosition()
        => config.EnableRagdoll && ragdollController.IsActive ? ragdollController.GetBodyWorldPosition("j_kosi") : null;
}
