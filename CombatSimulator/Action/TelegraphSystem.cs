using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

/// <summary>
/// A live enemy attack telegraph: a danger circle snapshotted at the target's
/// position when the attack was committed. The player can avoid it by leaving
/// the circle or by timing guard during the active frame.
/// </summary>
public sealed class ActiveTelegraph
{
    public SimulatedNpc Source = null!;
    public NpcAttackRequest Request;
    public Vector3 AnchorPos;
    public float Radius;
    public float WindupTotal;
    public float WindupElapsed;
    public bool Resolved;
    public float RecoveryRemaining;

    public float Progress => WindupTotal <= 0f ? 1f : Math.Clamp(WindupElapsed / WindupTotal, 0f, 1f);
}

/// <summary>
/// Owns active enemy telegraphs. Each ticks windup -> resolve -> recovery.
/// At the active frame the hit is tested against the target's live position.
/// Hits reuse CombatEngine.ProcessNpcAction for damage and visuals.
/// </summary>
public sealed class TelegraphSystem
{
    private const float MeleeStrikeRadius = 2.5f;
    private const float RecoveryDuration = 0.3f;

    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly Func<bool> playerGuardActive;
    private readonly Action playerPerfectGuard;
    private readonly IPluginLog log;
    private readonly List<ActiveTelegraph> active = new();

    public IReadOnlyList<ActiveTelegraph> Active => active;

    public TelegraphSystem(
        CombatEngine combatEngine,
        AnimationController animationController,
        Func<bool> playerGuardActive,
        Action playerPerfectGuard,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.playerGuardActive = playerGuardActive;
        this.playerPerfectGuard = playerPerfectGuard;
        this.log = log;
    }

    public void Spawn(SimulatedNpc source, in NpcAttackRequest req, float windup)
    {
        var target = combatEngine.State.GetEntity(req.TargetId);
        if (target == null || !target.IsAlive)
            return;

        animationController.PlayEnemyTelegraphWarning(source);

        active.Add(new ActiveTelegraph
        {
            Source = source,
            Request = req,
            AnchorPos = combatEngine.GetSimulatedEntityPosition(target),
            Radius = req.Radius > 0 ? req.Radius : MeleeStrikeRadius,
            WindupTotal = MathF.Max(0.01f, windup),
            WindupElapsed = 0f,
        });
    }

    public void Tick(float dt)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var t = active[i];
            if (!t.Resolved)
            {
                t.WindupElapsed += dt;
                if (t.WindupElapsed >= t.WindupTotal)
                {
                    Resolve(t);
                    t.Resolved = true;
                    t.RecoveryRemaining = RecoveryDuration;
                }
            }
            else
            {
                t.RecoveryRemaining -= dt;
                if (t.RecoveryRemaining <= 0f)
                    active.RemoveAt(i);
            }
        }
    }

    private void Resolve(ActiveTelegraph t)
    {
        var target = combatEngine.State.GetEntity(t.Request.TargetId);
        if (target == null || !target.IsAlive || !t.Source.State.IsAlive)
            return;

        var targetPos = combatEngine.GetSimulatedEntityPosition(target);
        var inside = CombatGeometry.IsInsideCircle(t.AnchorPos, targetPos, t.Radius);
        var guarded = inside && target.IsPlayer && playerGuardActive();

        if (guarded)
        {
            playerPerfectGuard();
            if (t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
                animationController.PlayNpcMeleeAnimationOnly(t.Source);
            return;
        }

        if (inside)
        {
            combatEngine.ProcessNpcAction(
                t.Source, t.Request.ActionId, t.Request.TargetId,
                t.Request.Potency, t.Request.Style, t.Request.Radius);
        }
        else if (t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
        {
            animationController.PlayNpcMeleeAnimationOnly(t.Source);
        }
    }

    /// <summary>
    /// True while the given enemy is mid-windup on a telegraph. Used to suppress
    /// hit-reaction flinch so committed enemy attacks cannot be stunlocked.
    /// </summary>
    public bool IsWindingUp(uint npcSimId)
    {
        foreach (var t in active)
            if (!t.Resolved && t.Source.SimulatedEntityId == npcSimId)
                return true;
        return false;
    }

    public void Clear() => active.Clear();
}
