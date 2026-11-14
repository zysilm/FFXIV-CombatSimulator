// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Animation;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Combat;

/// <summary>
/// The game side of <see cref="ICombatWorld"/>: each member is the pointer operation (or the call) the
/// combat loops made in that place before stage B4a, unchanged.
/// </summary>
public sealed unsafe class GameCombatWorld : ICombatWorld
{
    private readonly IObjectTable objectTable;
    private readonly ITargetManager? targetManager;
    private readonly AnimationController animationController;
    private readonly MovementBlockHook movementBlockHook;
    private readonly NpcCorpseFootIkSolver? footIkSolver;

    public GameCombatWorld(
        IObjectTable objectTable,
        ITargetManager? targetManager,
        AnimationController animationController,
        MovementBlockHook movementBlockHook,
        NpcCorpseFootIkSolver? footIkSolver)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.animationController = animationController;
        this.movementBlockHook = movementBlockHook;
        this.footIkSolver = footIkSolver;
    }

    /// <summary>Dalamud's object wrapper, read member by member when asked (as the loops read it).</summary>
    private sealed class ObjectView(IGameObject obj) : IGameObjectView
    {
        public nint Address => obj.Address;
        public uint EntityId => obj.EntityId;
        public Vector3 Position => obj.Position;
        public float Rotation => obj.Rotation;
    }

    public IGameObjectView? LocalPlayer => objectTable.LocalPlayer is { } player ? new ObjectView(player) : null;
    /// <summary>Ally adapter only (the enemies' is built without a target manager).</summary>
    public IGameObjectView? PlayerTarget => TargetManager.Target is { } target ? new ObjectView(target) : null;

    private ITargetManager TargetManager => targetManager ?? throw new NotSupportedException(
        "ICombatWorld.PlayerTarget: this GameCombatWorld was built without a target manager (the enemies' adapter); only the allies' supports it.");

    private NpcCorpseFootIkSolver FootIk => footIkSolver ?? throw new NotSupportedException(
        "ICombatWorld foot IK (TrackFootIk / FootIkPelvisOffset / ReportFootIkRootLift): this GameCombatWorld was built without a corpse foot solver (the allies' adapter); only the enemies' supports it.");

    public Vector3 PositionOf(Combatant c) => ((GameObject*)c.BattleChara)->Position;
    public Vector3 ReferencePositionOf(Combatant c) => c.GameObjectRef!.Position;
    public float RotationOf(Combatant c) => ((GameObject*)c.BattleChara)->Rotation;
    public float HitboxRadiusOf(Combatant c) => c.HitboxRadius;
    public float HitboxRadiusAt(nint address) => ((GameObject*)address)->HitboxRadius;
    public uint EntityIdOf(Combatant c) => ((GameObject*)c.BattleChara)->EntityId;
    public GameObjectId GameObjectIdAt(nint address) => ((GameObject*)address)->GetGameObjectId();
    public uint TargetObjectIdOf(Combatant c) => ((Character*)c.BattleChara)->TargetId.ObjectId;

    public void SetTargetId(Combatant c, GameObjectId id) => ((Character*)c.BattleChara)->TargetId = id;
    public void SetRotation(Combatant c, float rotation) => ((GameObject*)c.BattleChara)->Rotation = rotation;
    public void SetEntityId(Combatant c, uint entityId) => ((GameObject*)c.BattleChara)->EntityId = entityId;

    public void SetNormalMode(Combatant c)
    {
        var character = (Character*)c.BattleChara;
        character->Mode = CharacterModes.Normal;
        character->ModeParam = 0;
    }

    public void AddApproachNpc(nint address) => movementBlockHook.AddApproachNpc(address);
    public void RemoveApproachNpc(nint address) => movementBlockHook.RemoveApproachNpc(address);
    public void ClearApproachNpcs() => movementBlockHook.ClearApproachNpcs();
    public void ClearApproachNpcsExcept(Func<nint, bool> keep) => movementBlockHook.ClearApproachNpcsExcept(keep);
    public void SetApproachPosition(Combatant c, float x, float y, float z)
        => movementBlockHook.SetApproachPosition((GameObject*)c.BattleChara, x, y, z);
    public void SetApproachRotation(Combatant c, float rotation)
        => movementBlockHook.SetApproachRotation((GameObject*)c.BattleChara, rotation);

    public void ApplyMoving(Combatant c, float deltaTime)
        => ActorVisualStateController.ApplyMoving((Character*)c.BattleChara, c.VisualState, deltaTime);
    public void ApplyMoving(Combatant c, float deltaTime, ushort timelineId)
        => ActorVisualStateController.ApplyMoving((Character*)c.BattleChara, c.VisualState, deltaTime, timelineId);
    public void ApplyCombatIdle(Combatant c) => ActorVisualStateController.ApplyCombatIdle((Character*)c.BattleChara, c.VisualState);
    public void ApplyActionLocked(Combatant c) => ActorVisualStateController.ApplyActionLocked((Character*)c.BattleChara, c.VisualState);
    public void ApplyDead(Combatant c) => ActorVisualStateController.ApplyDead((Character*)c.BattleChara, c.VisualState);
    public void ClearMovement(nint address, ActorVisualState visual) => ActorVisualStateController.ClearMovement((Character*)address, visual);

    public void SetBattleStance(SimulatedNpc npc) => animationController.SetBattleStance(npc);
    public void ClearBattleStance(SimulatedNpc npc) => animationController.ClearBattleStance(npc);
    public void PlayDeathAnimation(SimulatedNpc npc) => animationController.PlayDeathAnimation(npc);
    public void ResetDeathAnimation(Combatant c) => animationController.ResetDeathAnimation(c.BattleChara);
    public bool PlayEmoteOn(Combatant c, uint emoteId) => animationController.PlayEmoteOn((Character*)c.BattleChara, emoteId);
    public ushort EmoteLoopTimeline(uint emoteId) => animationController.EmoteLoopTimeline(emoteId);
    public void HoldEmoteLoop(Combatant c, ushort loop) => AnimationController.HoldEmoteLoop((Character*)c.BattleChara, loop);
    public void ResetEmote(Combatant c) => animationController.EmotePlayer.ResetEmote((Character*)c.BattleChara);

    // Enemy adapter only (the allies' is built without a corpse foot solver).
    public void TrackFootIk(nint address, float deltaTime) => FootIk.Track(address, deltaTime);
    public float FootIkPelvisOffset(nint address) => FootIk.GetPelvisOffset(address);
    public void ReportFootIkRootLift(nint address, float lift) => FootIk.ReportRootLift(address, lift);
}

/// <summary>
/// The game side of <see cref="IAttackSink"/>: the enemies' attack router and telegraph snapshot, and the
/// engine's companion action, called exactly as the loops called them before stage B4a.
/// </summary>
public sealed class GameAttackSink : IAttackSink
{
    private readonly CombatEngine combatEngine;
    private readonly CombatModeRouter? combatModeRouter;

    public GameAttackSink(CombatEngine combatEngine, CombatModeRouter? combatModeRouter)
    {
        this.combatEngine = combatEngine;
        this.combatModeRouter = combatModeRouter;
    }

    /// <summary>Enemy adapter only (the allies' is built without an attack router).</summary>
    public bool ExecuteEnemyAttack(SimulatedNpc npc, NpcAttackRequest request)
        => (combatModeRouter ?? throw new NotSupportedException(
                "IAttackSink.ExecuteEnemyAttack: this GameAttackSink was built without an attack router (the allies' adapter); only the enemies' supports it."))
            .AttackExecutor.Execute(npc, request);

    public TelegraphShape SnapshotShape(SimulatedNpc npc, NpcAttackRequest request, SimulatedEntityState target)
        => TelegraphSystem.SnapshotShape(combatEngine, npc, request, target);

    public SimulatedActionResult ProcessCompanionAction(CombatCompanion companion, SimulatedNpc target, uint actionId,
        int potency = 0, NpcAttackStyle attackStyle = NpcAttackStyle.Auto, float radius = 0)
        => combatEngine.ProcessCompanionAction(companion, target, actionId, potency, attackStyle, radius);
}
