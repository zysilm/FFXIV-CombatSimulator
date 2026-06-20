using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.ActionCombat;

public enum TelegraphOutcome
{
    Pending,
    Guarded,
    Hit,
    Dodged,
}

/// <summary>
/// A live enemy attack telegraph: a danger circle snapshotted at the target's position
/// when committed. The osu-style approach circle reads off <see cref="Progress"/> and the
/// grace state. The enemy's real attack (swing + impact sound + hit-reaction) only fires at
/// the strike moment via <see cref="CombatEngine.ProcessNpcAction"/>, so it stays in sync
/// with the circle closing. Guard is double-sided: a press lands if the guard window is active
/// at the strike (early tolerance) or within the post-strike grace (late tolerance).
/// </summary>
public sealed class ActiveTelegraph
{
    public SimulatedNpc Source = null!;
    public NpcAttackRequest Request;
    public Vector3 AnchorPos;
    public float Radius;
    public float LeadInTotal;
    public float LeadInElapsed;
    public float WindupTotal;
    public float WindupElapsed;
    public bool Resolved;
    public float RecoveryRemaining;
    public bool TargetIsPlayer;
    public TelegraphOutcome Outcome;
    public bool WindupAnimationPlayed;
    // After the circle closes (perfect moment), a short window where a late guard still counts.
    public bool InGrace;
    public float GraceRemaining;
    public float GraceTotal;

    public bool IsApproachActive => LeadInElapsed >= LeadInTotal;
    public float Progress => !IsApproachActive || WindupTotal <= 0f
        ? 0f
        : Math.Clamp(WindupElapsed / WindupTotal, 0f, 1f);
}

/// <summary>
/// Owns active enemy telegraphs: windup → strike (with a late-guard grace) → recovery.
/// The hit is tested against the target's live position; hits reuse CombatEngine.ProcessNpcAction
/// so the full attack (swing, impact sound, hit-reaction, flytext) plays at the strike.
/// </summary>
public sealed class TelegraphSystem
{
    private const float MeleeStrikeRadius = 2.5f;
    private const float RecoveryDuration = 0.3f;

    private readonly CombatEngine combatEngine;
    private readonly AnimationController animationController;
    private readonly Configuration config;
    private readonly Func<bool> playerGuardActive;
    private readonly Action playerPerfectGuard;
    private readonly IPluginLog log;
    private readonly List<ActiveTelegraph> active = new();

    public IReadOnlyList<ActiveTelegraph> Active => active;

    public TelegraphSystem(
        CombatEngine combatEngine,
        AnimationController animationController,
        Configuration config,
        Func<bool> playerGuardActive,
        Action playerPerfectGuard,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.config = config;
        this.playerGuardActive = playerGuardActive;
        this.playerPerfectGuard = playerPerfectGuard;
        this.log = log;
    }

    public void Spawn(SimulatedNpc source, in NpcAttackRequest req, float windup, float leadIn = 0f)
    {
        var target = combatEngine.State.GetEntity(req.TargetId);
        if (target == null || !target.IsAlive)
            return;

        var normalizedLeadIn = MathF.Max(0f, leadIn);
        var windupAnimationPlayed = false;
        if (normalizedLeadIn <= 0f)
            windupAnimationPlayed = TryPlayWindupAnimation(source, req);

        // The enemy's real swing fires at the strike (when the circle closes), so it stays
        // synced with the telegraph and keeps its impact sound/reaction.
        active.Add(new ActiveTelegraph
        {
            Source = source,
            Request = req,
            AnchorPos = combatEngine.GetSimulatedEntityPosition(target),
            Radius = req.Radius > 0 ? req.Radius : MeleeStrikeRadius,
            LeadInTotal = normalizedLeadIn,
            LeadInElapsed = normalizedLeadIn <= 0f ? 0f : 0f,
            WindupTotal = MathF.Max(0.01f, windup),
            WindupElapsed = 0f,
            TargetIsPlayer = target.IsPlayer,
            Outcome = TelegraphOutcome.Pending,
            WindupAnimationPlayed = windupAnimationPlayed,
        });
    }

    public void Tick(float dt)
    {
        var lateTol = MathF.Max(0f, config.GuardLateTolerance);

        for (int i = active.Count - 1; i >= 0; i--)
        {
            var t = active[i];

            if (t.Resolved)
            {
                t.RecoveryRemaining -= dt;
                if (t.RecoveryRemaining <= 0f)
                    active.RemoveAt(i);
                continue;
            }

            var target = combatEngine.State.GetEntity(t.Request.TargetId);
            if (target == null || !target.IsAlive || !t.Source.State.IsAlive)
            {
                Finish(t, TelegraphOutcome.Dodged);
                continue;
            }

            var windupDt = dt;
            if (!t.IsApproachActive)
            {
                t.LeadInElapsed += dt;
                if (!t.IsApproachActive)
                    continue;

                windupDt = MathF.Max(0f, t.LeadInElapsed - t.LeadInTotal);
                t.LeadInElapsed = t.LeadInTotal;
                if (!t.WindupAnimationPlayed)
                    t.WindupAnimationPlayed = TryPlayWindupAnimation(t.Source, t.Request);
                if (windupDt <= 0f)
                    continue;
            }

            t.WindupElapsed += windupDt;

            if (!t.InGrace)
            {
                if (t.WindupElapsed < t.WindupTotal)
                    continue;

                // Perfect moment — the outer ring has closed onto the inner ring.
                var inside = IsInside(t, target);
                if (inside && target.IsPlayer && playerGuardActive())
                {
                    DoGuard(t);
                    continue;
                }
                if (!inside)
                {
                    DoDodge(t);
                    continue;
                }
                // Inside but not guarding — open the late-guard grace before the strike lands.
                if (lateTol <= 0f)
                {
                    DoHit(t);
                    continue;
                }
                t.InGrace = true;
                t.GraceTotal = lateTol;
                t.GraceRemaining = lateTol;
            }
            else
            {
                if (IsInside(t, target) && target.IsPlayer && playerGuardActive())
                {
                    DoGuard(t);
                    continue;
                }
                t.GraceRemaining -= dt;
                if (t.GraceRemaining <= 0f)
                {
                    if (IsInside(t, target))
                        DoHit(t);
                    else
                        DoDodge(t);
                }
            }
        }
    }

    private bool IsInside(ActiveTelegraph t, SimulatedEntityState target)
        => CombatGeometry.IsInsideCircle(t.AnchorPos, combatEngine.GetSimulatedEntityPosition(target), t.Radius);

    private void DoGuard(ActiveTelegraph t)
    {
        playerPerfectGuard();
        if (!t.WindupAnimationPlayed)
            PlayWhiffSwing(t);
        Finish(t, TelegraphOutcome.Guarded);
    }

    private void DoHit(ActiveTelegraph t)
    {
        // Full attack: swing + impact sound + target hit-reaction + flytext all fire here, in
        // sync with the circle closing. (Same path normal mode uses, so feedback matches.) The
        // engine ties the sound/hit-react to a swing, so on a HIT this is the one case that can
        // briefly double-swing when a windup swing already played.
        combatEngine.ProcessNpcAction(
            t.Source, t.Request.ActionId, t.Request.TargetId,
            t.Request.Potency, t.Request.Style, t.Request.Radius);
        Finish(t, TelegraphOutcome.Hit);
    }

    private void DoDodge(ActiveTelegraph t)
    {
        if (!t.WindupAnimationPlayed)
            PlayWhiffSwing(t);
        Finish(t, TelegraphOutcome.Dodged);
    }

    private void PlayWhiffSwing(ActiveTelegraph t)
    {
        if (t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
            animationController.PlayNpcMeleeAnimationOnly(t.Source);
    }

    private bool TryPlayWindupAnimation(SimulatedNpc source, in NpcAttackRequest req)
        => config.ActionEnemyWindupSwing &&
           req.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto &&
           animationController.PlayNpcWindupPose(source, req.ActionId);

    private void Finish(ActiveTelegraph t, TelegraphOutcome outcome)
    {
        t.Outcome = outcome;
        t.InGrace = false;
        t.Resolved = true;
        t.RecoveryRemaining = RecoveryDuration;
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
