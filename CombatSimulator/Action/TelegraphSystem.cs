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
/// position when the attack was committed (起手快照). The player dodges by leaving
/// the circle before the windup completes.
/// </summary>
public sealed class ActiveTelegraph
{
    public SimulatedNpc Source = null!;
    public NpcAttackRequest Request;
    public Vector3 AnchorPos;     // snapshot — does NOT follow the target
    public float Radius;
    public float WindupTotal;
    public float WindupElapsed;
    public bool Resolved;
    public float RecoveryRemaining;

    public float Progress => WindupTotal <= 0f ? 1f : Math.Clamp(WindupElapsed / WindupTotal, 0f, 1f);
}

/// <summary>
/// Owns active enemy telegraphs. Each ticks Windup → (resolve) → Recovery. At the
/// active frame the hit is tested with <see cref="CombatGeometry"/> against the
/// target's LIVE position — the same circle the overlay draws — so a dodge is a real
/// miss. Hits reuse <see cref="CombatEngine.ProcessNpcAction"/> (damage + visuals).
/// </summary>
public sealed class TelegraphSystem
{
    private const float MeleeStrikeRadius = 2.5f;
    private const float RecoveryDuration = 0.3f;

    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly Func<bool> playerInvulnerable;
    private readonly IPluginLog log;
    private readonly List<ActiveTelegraph> active = new();

    public IReadOnlyList<ActiveTelegraph> Active => active;

    public TelegraphSystem(
        CombatEngine combatEngine,
        AnimationController animationController,
        Func<bool> playerInvulnerable,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.playerInvulnerable = playerInvulnerable;
        this.log = log;
    }

    public void Spawn(SimulatedNpc source, in NpcAttackRequest req, float windup)
    {
        var target = combatEngine.State.GetEntity(req.TargetId);
        if (target == null || !target.IsAlive)
            return;

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
        bool inside = CombatGeometry.IsInsideCircle(t.AnchorPos, targetPos, t.Radius);
        bool dodged = target.IsPlayer && playerInvulnerable();

        if (inside && !dodged)
        {
            // Reuse the engine's apply-damage + animation pipeline (no range gate).
            combatEngine.ProcessNpcAction(
                t.Source, t.Request.ActionId, t.Request.TargetId,
                t.Request.Potency, t.Request.Style, t.Request.Radius);
        }
        else if (t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
        {
            // Whiff: melee enemies still swing so the dodge reads clearly. Ranged
            // whiffs simply don't fire a projectile.
            animationController.PlayNpcMeleeAnimationOnly(t.Source);
        }
    }

    /// <summary>
    /// True while the given enemy is mid-windup on a telegraph. Used to grant
    /// super-armor (suppress the hit-reaction flinch) so a committed enemy attack
    /// isn't visually stunlocked by rapid player hits — the player must dodge, not mash.
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
