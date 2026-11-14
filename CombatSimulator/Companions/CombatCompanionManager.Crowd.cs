// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Combat;
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
    public ICrowdAgents? Crowd { private get; set; }

    private bool CrowdReady => Crowd is { Ready: true };

    // The same standing rules as enemies (CombatantMath): melee closes to a 1 m gap, ranged stops
    // 1 m inside its reach; moving resumes only once the target is out of reach. Reach is
    // ReachPolicy.Ally: authored ranges, a melee auto-attack at least 3, a caster's swing 3.5.
    // A corpse lower than this under an ally's feet is stepped onto; a higher one is walked through.
    private const float CorpseStepLimit = 0.65f;

    // What the allies did, counted per frame and written to the log every few seconds of a fight, so
    // an ally standing about can be explained from the log.
    private const int ActivityFighting = 0, ActivityWalkingIn = 1, ActivityNoTarget = 2, ActivityReturning = 3, ActivityOther = 4;
    private static readonly string[] ActivityNames = { "in reach", "walking in", "no target", "back to the lord", "praying/raising/captive" };
    private readonly long[] activityFrames = new long[5];
    private readonly System.Diagnostics.Stopwatch activityClock = System.Diagnostics.Stopwatch.StartNew();

    private void NoteActivity(int activity) => activityFrames[activity]++;

    private void ReportActivity(System.Collections.Generic.IReadOnlyList<SimulatedNpc> enemies)
    {
        if (activityClock.Elapsed.TotalSeconds < 5)
            return;
        activityClock.Restart();
        long total = 0;
        foreach (var n in activityFrames) total += n;
        if (total == 0)
            return;
        var parts = new System.Collections.Generic.List<string>();
        for (var i = 0; i < activityFrames.Length; i++)
            if (activityFrames[i] > 0)
                parts.Add($"{ActivityNames[i]} {100.0 * activityFrames[i] / total:0}%");
        var living = 0;
        foreach (var e in enemies) if (e.IsSpawned && e.State.IsAlive) living++;
        log.Info($"Allies (share of ally-frames over 5 s, {living} enemies alive): {string.Join(", ", parts)}.");
        System.Array.Clear(activityFrames);
    }

    private float HitboxOf(Combatant self)
        => self.BattleChara != null && world.HitboxRadiusAt(self.Address) > 0.01f ? world.HitboxRadiusAt(self.Address) : EnemyMovementMath.BaselineHitboxRadius;

    /// <summary>Horizontal space between the two bodies: how ranges are measured in the game.</summary>
    private float CrowdGap(CombatCompanion companion, SimulatedNpc target)
    {
        return CombatantMath.BodyGap(world.PositionOf(companion), world.PositionOf(target), HitboxOf(companion), world.HitboxRadiusOf(target));
    }

    /// <summary>
    /// How far this ally can attack from right now: its auto-attack (a caster's is a melee swing), or the
    /// longest skill it could use this moment (off cooldown, affordable).
    /// </summary>
    private float CrowdReachNow(CombatCompanion companion)
        => CombatantMath.ReachNow(companion.Behavior, companion.State, ReachPolicy.Ally, default, mpPriceOf ??= MpPriceOf);

    private System.Func<uint, int>? mpPriceOf;

    /// <summary>A skill's price against the player's pool (0 when the action is unknown).</summary>
    private int MpPriceOf(uint actionId) => combatEngine.GetActionData(actionId)?.MpCost ?? 0;

    /// <summary>
    /// Decide whether to close in on the target: true (and the ally walks) while it is out of reach,
    /// false once it is close enough to fight, with the same hysteresis as enemies.
    /// </summary>
    private bool CrowdApproach(CombatCompanion companion, SimulatedNpc target, float deltaTime)
    {
        var reach = CrowdReachNow(companion);
        var gap = CrowdGap(companion, target);
        var stand = CombatantMath.StandGap(reach);

        companion.InReach = CombatantMath.NextInReach(companion.InReach, gap, reach, stand);
        if (companion.InReach)
            return false;

        var self = world.PositionOf(companion);
        var targetPos = world.PositionOf(target);
        var goal = CombatantMath.StandPoint(self, targetPos, stand + HitboxOf(companion) + world.HitboxRadiusOf(target));
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

        var speed = CombatantMath.MoveSpeed(companion.Behavior.MoveSpeed, MovePolicy.Ally);
        if (!crowd.Drive(companion.Address, world.PositionOf(companion), HitboxOf(companion), speed, goal))
            return false;
        if (!crowd.TryGetMotion(companion.Address, out var position, out var velocity))
            return true;

        // Standing on a corpse: up onto it when it is low enough to step on.
        if (config.NpcCollisionActive &&
            CorpseSupportHeightProvider?.Invoke(companion.Address, position) is { } support &&
            support > position.Y && support - position.Y <= CorpseStepLimit)
            position.Y = support;

        world.AddApproachNpc(companion.Address);
        world.SetApproachPosition(companion, position.X, position.Y, position.Z);
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
