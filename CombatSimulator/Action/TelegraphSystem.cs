// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
    /// <summary>Action row that authored the selected model-family windup; used only for VFX.</summary>
    public uint ModelVfxActionId;
    /// <summary>The selected model-family timeline scheduled its own native VFX track.</summary>
    public bool ModelTimelineOwnsVfx;
    /// <summary>Cast/channel VFX already spawned when this telegraph began — the strike skips it.</summary>
    public bool CastVfxPlayed;
    // After the circle closes (perfect moment), a short window where a late guard still counts.
    public bool InGrace;
    public float GraceRemaining;
    public float GraceTotal;
    /// <summary>Real-shape snapshot (only when real-shape telegraphs are on); see <see cref="TelegraphShape"/>.</summary>
    public TelegraphShape Shape;
    public bool HasShape;
    public int MarkerId;

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
    private readonly EnemyAreaMarkers markers;
    private readonly List<ActiveTelegraph> active = new();

    public IReadOnlyList<ActiveTelegraph> Active => active;

    public TelegraphSystem(
        CombatEngine combatEngine,
        AnimationController animationController,
        Configuration config,
        Func<bool> playerGuardActive,
        Action playerPerfectGuard,
        IPluginLog log,
        EnemyAreaMarkers markers)
    {
        this.combatEngine = combatEngine;
        this.animationController = animationController;
        this.config = config;
        this.playerGuardActive = playerGuardActive;
        this.playerPerfectGuard = playerPerfectGuard;
        this.log = log;
        this.markers = markers;
    }

    public void Spawn(SimulatedNpc source, in NpcAttackRequest req, float windup, float leadIn = 0f)
    {
        var target = combatEngine.State.GetEntity(req.TargetId);
        if (target == null || !target.IsAlive)
            return;

        var normalizedLeadIn = MathF.Max(0f, leadIn);
        var windupAnimationPlayed = false;
        var modelVfxActionId = 0u;
        var modelTimelineOwnsVfx = false;
        if (normalizedLeadIn <= 0f)
            windupAnimationPlayed = TryPlayWindupAnimation(
                source,
                req,
                out modelVfxActionId,
                out modelTimelineOwnsVfx);

        // Ranged/magic skills get no windup swing, so nothing marks the cast visually until the
        // strike — where the cast VFX spawned and vanished in the same instant. Light the channel
        // up front so the caster visibly casts for the whole windup; the strike then skips it.
        var castVfxPlayed = false;
        if (!req.IsAutoAttack && req.Style is NpcAttackStyle.Ranged or NpcAttackStyle.Magic)
        {
            var castData = combatEngine.GetActionData(req.ActionId);
            if (castData != null)
                castVfxPlayed = animationController.PlayNpcCastVfx(source, castData);
        }

        // Real shape: snapshot the action's own area now (origin, facing, size), draw it, and hit
        // against exactly it at the strike. Off ⇒ the legacy circle on the target below.
        var hasShape = false;
        var shape = default(TelegraphShape);
        var markerId = 0;
        if (config.EnemyTelegraphRealShapes)
        {
            shape = SnapshotShape(combatEngine, source, req, target);
            hasShape = true;
            markerId = markers.Add(source.SimulatedEntityId, req.ActionId, shape, normalizedLeadIn + MathF.Max(0.01f, windup));
        }

        // The enemy's real swing fires at the strike (when the circle closes), so it stays
        // synced with the telegraph and keeps its impact sound/reaction.
        active.Add(new ActiveTelegraph
        {
            Shape = shape,
            HasShape = hasShape,
            MarkerId = markerId,
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
            ModelVfxActionId = modelVfxActionId,
            ModelTimelineOwnsVfx = modelTimelineOwnsVfx,
            CastVfxPlayed = castVfxPlayed,
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
                    t.WindupAnimationPlayed = TryPlayWindupAnimation(
                        t.Source,
                        t.Request,
                        out t.ModelVfxActionId,
                        out t.ModelTimelineOwnsVfx);
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
    {
        var pos = combatEngine.GetSimulatedEntityPosition(target);
        return t.HasShape
            ? t.Shape.Contains(pos)
            : CombatGeometry.IsInsideCircle(t.AnchorPos, pos, t.Radius);
    }

    /// <summary>Real-shape areas resolve everyone standing in them, not just the primary.</summary>
    private static bool HasArea(ActiveTelegraph t) => t.HasShape && t.Shape.IsArea;

    /// <summary>
    /// Snapshot the area of an enemy attack against <paramref name="target"/>, mirroring how
    /// <see cref="CombatEngine.ProcessNpcAction"/> reads the action (a sized single-target skill
    /// becomes a caster circle there, so it does here too).
    /// </summary>
    public static TelegraphShape SnapshotShape(
        CombatEngine engine, SimulatedNpc source, in NpcAttackRequest req, SimulatedEntityState target)
    {
        var data = req.IsAutoAttack ? null : engine.GetActionData(req.ActionId);
        var shape = data?.Shape ?? AoeShape.Single;
        var radius = req.Radius > 0f ? req.Radius : data?.Radius ?? 0f;
        if (req.Radius > 0f && shape == AoeShape.Single)
            shape = AoeShape.CircleSelf;

        return TelegraphShape.Build(
            shape,
            data?.CastType ?? 1,
            radius,
            data?.Width ?? 0f,
            data?.ConeAngleDeg ?? CastTypeMapping.DefaultConeAngleDeg,
            data?.InnerRadius ?? 0f,
            engine.GetSimulatedEntityPosition(source.State),
            source.HitboxRadius,
            engine.GetSimulatedEntityPosition(target),
            req.Radius > 0 ? req.Radius : MeleeStrikeRadius);
    }

    private SimulatedActionResult ResolveStrike(ActiveTelegraph t, ulong excludeTargetId = 0)
        => combatEngine.ProcessNpcAction(
            t.Source, t.Request.ActionId, t.Request.TargetId,
            t.Request.Potency, t.Request.Style, t.Request.Radius,
            suppressCasterActionEffect: t.WindupAnimationPlayed,
            suppressCastVfx: t.CastVfxPlayed,
            modelVfxActionId: t.ModelVfxActionId,
            modelTimelineOwnsVfx: t.ModelTimelineOwnsVfx,
            area: HasArea(t) ? t.Shape : null,
            excludeTargetId: excludeTargetId);

    private void DoGuard(ActiveTelegraph t)
    {
        playerPerfectGuard();
        // The guard protects the player, not the whole party: companions still standing in a
        // real area take the hit (the full swing then plays through the strike).
        var struckOthers = HasArea(t) && ResolveStrike(t, t.Request.TargetId).Success;
        if (!t.WindupAnimationPlayed && !struckOthers)
            PlayWhiffSwing(t);
        Finish(t, TelegraphOutcome.Guarded);
    }

    private void DoHit(ActiveTelegraph t)
    {
        // Full attack: swing + impact sound + target hit-reaction + flytext all fire here, in
        // sync with the circle closing. (Same path normal mode uses, so feedback matches.) The
        // engine ties the sound/hit-react to a swing, so on a HIT this is the one case that can
        // briefly double-swing when a windup swing already played.
        ResolveStrike(t);
        Finish(t, TelegraphOutcome.Hit);
    }

    private void DoDodge(ActiveTelegraph t)
    {
        // The primary stepped out, but a real area still lands on whoever else is inside it.
        var struckOthers = HasArea(t) && ResolveStrike(t).Success;
        if (!t.WindupAnimationPlayed && !struckOthers)
            PlayWhiffSwing(t);
        Finish(t, TelegraphOutcome.Dodged);
    }

    private void PlayWhiffSwing(ActiveTelegraph t)
    {
        // Show the enemy attack even when it's blocked/dodged. Ranged/magic play their cast pose
        // (no projectile/damage) so a parried staff enemy isn't animation-less.
        // A real skill whiffs with its own timeline; the generic melee whiff picks a random one
        // from the model family, which on a monster is some other skill's animation.
        if (config.EnemyMonsterRealSkills && !t.Request.IsAutoAttack &&
            t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
            animationController.PlayNpcWindupPose(t.Source, t.Request.ActionId);
        else if (t.Request.Style is NpcAttackStyle.Melee or NpcAttackStyle.Auto)
            animationController.PlayNpcMeleeAnimationOnly(t.Source);
        else
            animationController.PlayNpcWindupPose(t.Source, t.Request.ActionId);
    }

    // Physical melee swings (auto-attacks AND melee weaponskills) play their real animation at the
    // START of the windup and get single-swing suppression at the strike — so the swing reads as a
    // wind-up instead of firing after the circle closes. Ranged/magic keep the full ActionEffect at
    // the strike so their projectile/cast VFX and damage fire together (a windup pose would launch
    // the projectile early with no damage). A humanoid enemy plays the real weaponskill because it
    // shares the player skeleton and wields a real weapon; monsters never reach here (auto-only).
    private bool TryPlayWindupAnimation(
        SimulatedNpc source,
        in NpcAttackRequest req,
        out uint modelVfxActionId,
        out bool modelTimelineOwnsVfx)
    {
        modelVfxActionId = 0;
        modelTimelineOwnsVfx = false;
        if (!config.ActionEnemyWindupSwing)
            return false;

        var isPhysicalSwing = req.Style == NpcAttackStyle.Melee ||
                              (req.IsAutoAttack && req.Style != NpcAttackStyle.Ranged);
        return isPhysicalSwing &&
               animationController.PlayNpcWindupPose(
                   source,
                   req.ActionId,
                   req.TargetId <= uint.MaxValue ? (uint)req.TargetId : 0,
                   out modelVfxActionId,
                   out modelTimelineOwnsVfx);
    }

    private void Finish(ActiveTelegraph t, TelegraphOutcome outcome)
    {
        markers.Remove(t.MarkerId);
        t.MarkerId = 0;
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

    /// <summary>
    /// True while the enemy has ANY live telegraph (windup, late-guard grace, or post-strike
    /// recovery). The AI must not commit a new attack while busy — doing so on the old, too-short
    /// animation lock is what let a second swing overlap the first (the "double attack").
    /// </summary>
    public bool IsBusy(uint npcSimId)
    {
        foreach (var t in active)
            if (t.Source.SimulatedEntityId == npcSimId)
                return true;
        return false;
    }

    public void Clear()
    {
        foreach (var t in active)
            markers.Remove(t.MarkerId);
        active.Clear();
    }
}
