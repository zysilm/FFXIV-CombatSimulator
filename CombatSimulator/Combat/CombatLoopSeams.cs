// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.ActionCombat;
using CombatSimulator.Animation;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Combat;

// The narrow seam between the two combat loops (NpcAiController, CombatCompanionManager) and the game
// (docs/PLUGIN_ARCHITECTURE.md §9, stage B4a). Every read and write of a game object those loops make
// goes through one of these; GameCombatWorld and GameAttackSink do exactly the pointer operations the
// loops did before, and Tests/CombatReplay gives a flat fake world instead. Nothing here decides
// anything: each member is one game operation.

/// <summary>A game object as the loops read it (today Dalamud's object wrapper: the local player, the player's target).</summary>
public interface IGameObjectView
{
    nint Address { get; }
    uint EntityId { get; }
    Vector3 Position { get; }
    float Rotation { get; }
}

/// <summary>
/// Every read and write of a game object the two combat loops make.
/// <para>Which side's game adapter supports what (<c>GameCombatWorld</c>): the allies'
/// (CombatCompanionManager) has no corpse foot solver, so <see cref="TrackFootIk"/>,
/// <see cref="FootIkPelvisOffset"/> and <see cref="ReportFootIkRootLift"/> are enemy-only; the enemies'
/// (NpcAiController) has no target manager, so <see cref="PlayerTarget"/> is ally-only. Every other member
/// works on both. An unsupported member throws <see cref="NotSupportedException"/> naming the member.</para>
/// </summary>
public interface ICombatWorld
{
    /// <summary>The local player (<c>ObjectTable.LocalPlayer</c>), or null.</summary>
    IGameObjectView? LocalPlayer { get; }
    /// <summary>What the player has selected (<c>TargetManager.Target</c>), or null. Ally adapter only.</summary>
    IGameObjectView? PlayerTarget { get; }

    // Reads of a combatant's game object.
    /// <summary><c>((GameObject*)c.BattleChara)->Position</c>.</summary>
    Vector3 PositionOf(Combatant c);
    /// <summary><c>c.GameObjectRef!.Position</c> (Dalamud's managed reference; may throw).</summary>
    Vector3 ReferencePositionOf(Combatant c);
    /// <summary><c>((GameObject*)c.BattleChara)->Rotation</c>.</summary>
    float RotationOf(Combatant c);
    /// <summary><c>c.HitboxRadius</c> (its game object's radius, 0.5 when none).</summary>
    float HitboxRadiusOf(Combatant c);
    /// <summary><c>((GameObject*)address)->HitboxRadius</c>.</summary>
    float HitboxRadiusAt(nint address);
    /// <summary><c>((GameObject*)c.BattleChara)->EntityId</c>.</summary>
    uint EntityIdOf(Combatant c);
    /// <summary><c>((GameObject*)address)->GetGameObjectId()</c>.</summary>
    GameObjectId GameObjectIdAt(nint address);
    /// <summary><c>((Character*)c.BattleChara)->TargetId.ObjectId</c>.</summary>
    uint TargetObjectIdOf(Combatant c);

    // Writes to a combatant's game object.
    /// <summary><c>((Character*)c.BattleChara)->TargetId = id</c>.</summary>
    void SetTargetId(Combatant c, GameObjectId id);
    /// <summary><c>((GameObject*)c.BattleChara)->Rotation = rotation</c>.</summary>
    void SetRotation(Combatant c, float rotation);
    /// <summary><c>((GameObject*)c.BattleChara)->EntityId = entityId</c>.</summary>
    void SetEntityId(Combatant c, uint entityId);
    /// <summary><c>Mode = CharacterModes.Normal; ModeParam = 0</c> on <c>(Character*)c.BattleChara</c>.</summary>
    void SetNormalMode(Combatant c);

    // MovementBlockHook: positions and facings the server must not undo.
    void AddApproachNpc(nint address);
    void RemoveApproachNpc(nint address);
    void ClearApproachNpcs();
    void ClearApproachNpcsExcept(Func<nint, bool> keep);
    /// <summary><c>SetApproachPosition((GameObject*)c.BattleChara, x, y, z)</c>.</summary>
    void SetApproachPosition(Combatant c, float x, float y, float z);
    /// <summary><c>SetApproachRotation((GameObject*)c.BattleChara, rotation)</c>.</summary>
    void SetApproachRotation(Combatant c, float rotation);

    // ActorVisualStateController on <c>(Character*)c.BattleChara</c> with <c>c.VisualState</c>.
    void ApplyMoving(Combatant c, float deltaTime);
    void ApplyMoving(Combatant c, float deltaTime, ushort timelineId);
    void ApplyCombatIdle(Combatant c);
    void ApplyActionLocked(Combatant c);
    void ApplyDead(Combatant c);
    /// <summary><c>ActorVisualStateController.ClearMovement((Character*)address, visual)</c>.</summary>
    void ClearMovement(nint address, ActorVisualState visual);

    // AnimationController.
    void SetBattleStance(SimulatedNpc npc);
    void ClearBattleStance(SimulatedNpc npc);
    void PlayDeathAnimation(SimulatedNpc npc);
    /// <summary><c>ResetDeathAnimation(c.BattleChara)</c>.</summary>
    void ResetDeathAnimation(Combatant c);
    /// <summary><c>PlayEmoteOn((Character*)c.BattleChara, emoteId)</c>.</summary>
    bool PlayEmoteOn(Combatant c, uint emoteId);
    ushort EmoteLoopTimeline(uint emoteId);
    /// <summary><c>AnimationController.HoldEmoteLoop((Character*)c.BattleChara, loop)</c>.</summary>
    void HoldEmoteLoop(Combatant c, ushort loop);
    /// <summary><c>EmotePlayer.ResetEmote((Character*)c.BattleChara)</c>.</summary>
    void ResetEmote(Combatant c);

    // NpcCorpseFootIkSolver (enemies standing on corpses). Enemy adapter only.
    void TrackFootIk(nint address, float deltaTime);
    float FootIkPelvisOffset(nint address);
    void ReportFootIkRootLift(nint address, float lift);
}

/// <summary>
/// Where an attack the loops commit is resolved (today the enemies' attack router and the engine's companion action).
/// <para>Which side's game adapter supports what (<c>GameAttackSink</c>): the allies' has no attack
/// router, so <see cref="ExecuteEnemyAttack"/> is enemy-only and throws <see cref="NotSupportedException"/>
/// there. <see cref="SnapshotShape"/> (used by the enemies) and <see cref="ProcessCompanionAction"/> (used by
/// the allies) only need the engine and work on both.</para>
/// </summary>
public interface IAttackSink
{
    /// <summary><c>combatModeRouter.AttackExecutor.Execute(npc, request)</c>. Enemy adapter only.</summary>
    bool ExecuteEnemyAttack(SimulatedNpc npc, NpcAttackRequest request);
    /// <summary><c>TelegraphSystem.SnapshotShape(combatEngine, npc, request, target)</c>.</summary>
    TelegraphShape SnapshotShape(SimulatedNpc npc, NpcAttackRequest request, SimulatedEntityState target);
    /// <summary><c>combatEngine.ProcessCompanionAction(companion, target, actionId, potency, style, radius)</c>.</summary>
    SimulatedActionResult ProcessCompanionAction(CombatCompanion companion, SimulatedNpc target, uint actionId,
        int potency = 0, NpcAttackStyle attackStyle = NpcAttackStyle.Auto, float radius = 0);
}

/// <summary>What the two loops use of <c>CombatEngine</c> besides attacks (implemented by CombatEngine itself).</summary>
public interface ICombatLoopEngine
{
    SimulationState State { get; }
    bool IsActive { get; }
    Battle.BattleRules Rules { get; }
    Action? OnSimulationStarted { get; set; }
    Action? OnSimulationReset { get; set; }
    Func<uint, nint?>? ResolveExternalEntityAddress { get; }
    Func<bool>? HasLivingCompanions { get; }
    bool IsEnemyInitiationSuppressed { get; }
    SimulatedEntityState GetNpcTarget(SimulatedNpc npc);
    Vector3 GetSimulatedEntityPosition(SimulatedEntityState entity);
    ActionData? GetActionData(uint actionId);
    void AddLogEntry(string message, CombatLogType type);
    void EnsurePlayerInitialized();
    void RegisterCompanionEntity(CombatCompanion companion);
    void UnregisterNpcEntity(uint entityId);
    void TriggerEnemyVictoryIfPartyDefeated();
    void TakeCaptive(SimulatedEntityState friend);
    void ReleaseCaptive(SimulatedEntityState friend, float hpShare);
    void RevivePlayerInPlace(float hpShare, bool refillMp);
    void PlaySupportVfx(SimulatedEntityState source, SimulatedEntityState target, uint actionId);
}

/// <summary>The crowd on the navigation mesh, as the loops drive it (implemented by Navigation.CrowdMover).</summary>
public interface ICrowdAgents
{
    bool Ready { get; }
    bool Drive(nint address, Vector3 position, float radius, float speed, Vector3? goal);
    bool TrySnapToFloor(Vector3 point, out Vector3 floor);
    void SetObstacle(nint address, Vector3 position, float radius);
    bool TryGetMotion(nint address, out Vector3 position, out Vector3 velocity);
}

/// <summary>Where a telegraphed cast's area is drawn (implemented by ActionCombat.EnemyAreaMarkers).</summary>
public interface ICastAreaMarkers
{
    int Add(uint sourceSimId, uint actionId, in TelegraphShape shape, float duration, Func<bool>? stillValid = null);
}
