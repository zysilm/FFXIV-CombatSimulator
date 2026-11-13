// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Companions;

/// <summary>
/// Allies on the plugin's navigation mesh (docs/AI_MOVEMENT_REWRITE.md): every move and stop goes
/// through the same DetourCrowd as the enemies, and an ally fights by the same reach and gap as they
/// do. There is no other mover: off the mesh an ally stands where it is.
/// </summary>
public unsafe partial class CombatCompanionManager
{
    /// <summary>The crowd on the navigation mesh; null or not ready: allies stand.</summary>
    public Navigation.CrowdMover? Crowd { private get; set; }

    private bool CrowdReady => Crowd is { Ready: true };

    // The same standing rules as enemies (NpcAiController): melee closes to a 1 m gap, ranged stops
    // 1 m inside its reach; moving resumes only once the target is out of reach.
    private const float CrowdMeleeStandGap = 1.0f;
    private const float CrowdRangedStandMargin = 1.0f;
    private const float CrowdStopTolerance = 0.25f;
    private const float CrowdMeleeReachLimit = 5f;
    private const float CrowdMagicAutoReach = 3.5f;
    // A corpse lower than this under an ally's feet is stepped onto; a higher one is walked through.
    private const float CorpseStepLimit = 0.65f;

    private static float HitboxOf(GameObject* obj)
        => obj != null && obj->HitboxRadius > 0.01f ? obj->HitboxRadius : EnemyMovementMath.BaselineHitboxRadius;

    /// <summary>Horizontal space between the two bodies: how ranges are measured in the game.</summary>
    private static float CrowdGap(CombatCompanion companion, SimulatedNpc target)
    {
        var self = (GameObject*)companion.BattleChara;
        var other = (GameObject*)target.BattleChara;
        return MathF.Max(0f, FlatDistance((Vector3)self->Position, (Vector3)other->Position) - HitboxOf(self) - target.HitboxRadius);
    }

    private static float SkillReach(CombatCompanion companion, NpcSkill skill) => skill.Range;

    /// <summary>
    /// How far this ally can attack from right now: its auto-attack (a caster's is a melee swing), or the
    /// longest skill it could use this moment (off cooldown, affordable).
    /// </summary>
    private float CrowdReachNow(CombatCompanion companion)
    {
        var reach = companion.Behavior.AutoAttackStyle switch
        {
            NpcAttackStyle.Ranged => companion.Behavior.AutoAttackRange,
            NpcAttackStyle.Magic => CrowdMagicAutoReach,
            _ => MathF.Max(companion.Behavior.AutoAttackRange, 3f),
        };
        foreach (var skill in companion.Behavior.Skills)
        {
            if (skill.CooldownRemaining > 0)
                continue;
            var mpCost = CombatBudgetModel.EnemySkillMpCost(combatEngine.GetActionData(skill.ActionId)?.MpCost ?? 0, companion.State.MaxMp);
            if (companion.State.CurrentMp < mpCost)
                continue;
            reach = MathF.Max(reach, SkillReach(companion, skill));
        }
        return reach;
    }

    /// <summary>
    /// Decide whether to close in on the target: true (and the ally walks) while it is out of reach,
    /// false once it is close enough to fight, with the same hysteresis as enemies.
    /// </summary>
    private bool CrowdApproach(CombatCompanion companion, SimulatedNpc target, float deltaTime)
    {
        var reach = CrowdReachNow(companion);
        var gap = CrowdGap(companion, target);
        var stand = reach <= CrowdMeleeReachLimit
            ? MathF.Min(CrowdMeleeStandGap, reach * 0.5f)
            : MathF.Max(CrowdMeleeStandGap, reach - CrowdRangedStandMargin);

        if (companion.InReach && gap > reach)
            companion.InReach = false;
        else if (!companion.InReach && gap <= stand + CrowdStopTolerance)
            companion.InReach = true;
        if (companion.InReach)
            return false;

        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;
        var targetPos = (Vector3)((GameObject*)target.BattleChara)->Position;
        var away = new Vector3(self.X - targetPos.X, 0f, self.Z - targetPos.Z);
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitZ;
        var goal = targetPos + away * (stand + HitboxOf((GameObject*)companion.BattleChara) + target.HitboxRadius);
        MoveToward(companion, goal, deltaTime);
        return true;
    }

    /// <summary>
    /// Move (or, with no goal, hold) this ally with the crowd, and put it where the crowd has it. False
    /// when there is no mesh, or the ally is not on it: it then stands.
    /// </summary>
    private bool TryCrowdMove(CombatCompanion companion, Vector3? goal, float deltaTime)
    {
        var crowd = Crowd;
        if (crowd == null || companion.BattleChara == null || !companion.State.IsAlive)
            return false;

        var obj = (GameObject*)companion.BattleChara;
        var speed = companion.Behavior.MoveSpeed > 0 ? companion.Behavior.MoveSpeed : 6f;
        if (!crowd.Drive(companion.Address, (Vector3)obj->Position, HitboxOf(obj), speed, goal))
            return false;
        if (!crowd.TryGetMotion(companion.Address, out var position, out var velocity))
            return true;

        // Standing on a corpse: up onto it when it is low enough to step on.
        if (config.NpcCollisionActive &&
            CorpseSupportHeightProvider?.Invoke(companion.Address, position) is { } support &&
            support > position.Y && support - position.Y <= CorpseStepLimit)
            position.Y = support;

        movementBlockHook.AddApproachNpc(companion.Address);
        movementBlockHook.SetApproachPosition(obj, position.X, position.Y, position.Z);
        var flat = new Vector3(velocity.X, 0f, velocity.Z);
        if (flat.LengthSquared() > 0.3f * 0.3f)
        {
            EnterCompanionState(
                companion,
                companion.CurrentTargetId == 0 ? CompanionAiState.ReturningToCommandRange : CompanionAiState.MovingToTarget,
                deltaTime);
            RotateToward(companion, position + flat, deltaTime);
        }
        else
            StopMoveAnim(companion);
        return true;
    }
}
