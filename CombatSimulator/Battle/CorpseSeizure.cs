// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Battle;

/// <summary>
/// What the corpse seizure needs from the battle (docs/PLUGIN_ARCHITECTURE.md §6.5): the lord's body,
/// the enemies, the retainers standing over the body, the battle's mechanics and its logs. Implemented
/// over the game by the plugin (<c>Plugin.War.cs</c>) and by a fake world in <c>Tests/CorpseSeizure</c>.
/// </summary>
public interface ICorpseSeizureWorld<TNpc> where TNpc : class
{
    /// <summary>The battle's outcome has been shown (the first word stands).</summary>
    bool OutcomeDecided { get; }

    /// <summary>
    /// Where the lord's body lies: its ragdoll's pelvis once it falls, else where the lord stands. Null
    /// when there is no lord (no local player).
    /// </summary>
    Vector3? LordBodyLocation();

    /// <summary>Every enemy in the battle, in the selector's order.</summary>
    IReadOnlyList<TNpc> Enemies { get; }

    /// <summary>Spawned, alive and with an actor.</summary>
    bool IsPresent(TNpc npc);
    bool IsCaptive(TNpc npc);
    /// <summary>A sentry that has not noticed the fight (idle).</summary>
    bool IsIdleSentry(TNpc npc);
    Vector3 PositionOf(TNpc npc);
    float HitboxRadiusOf(TNpc npc);
    string NameOf(TNpc npc);
    uint IdOf(TNpc npc);

    /// <summary>Where the enemy AI sends this enemy (the body), or null: back to the fight.</summary>
    void SetSeizeGoal(TNpc npc, Vector3? goal);
    /// <summary>Whether this enemy is held off at the body (it fights the guards there).</summary>
    void SetSeizeHeld(TNpc npc, bool held);
    /// <summary>Wakes the enemy if it stands idle.</summary>
    void Engage(TNpc npc);

    /// <summary>The seizers' ids, as the guards' target choice sees them.</summary>
    void ClearSeizerIds();
    void AddSeizerId(uint id);

    /// <summary>How many retainers who can still fight for it stand within <paramref name="radius"/> of the body.</summary>
    int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance);

    /// <summary>The lord's body has fallen and can be taken in hand (its ragdoll is up, or ragdolls are off).</summary>
    bool BodyReady { get; }

    /// <summary>The lord's body has been carried off (true), or the battle was reset (false).</summary>
    void SetCorpseCaptured(bool captured);
    /// <summary>Whoever of the lord's side is left is taken.</summary>
    void SurrenderRetainers();
    void ForceEnemyVictory();

    /// <summary>A line in the battle log (death type).</summary>
    void BattleLog(string text);
    void Info(string text);
    void Warning(string text);
}

/// <summary>The grab on the seized body (Enemy Control in the plugin, docs/PLUGIN_ARCHITECTURE.md §6.5).</summary>
public interface ICorpseGrab<TNpc> where TNpc : class
{
    /// <summary>The seizer is taken over at all (Enemy Control's "On death: Control killer"); off: the body is carried off, nobody is taken over.</summary>
    bool TakesOverSeizer { get; }
    /// <summary>The body can be held: ragdolls are on and the lord's ragdoll is up.</summary>
    bool BodyCanBeHeld { get; }
    /// <summary>An enemy is under control.</summary>
    bool IsControlling { get; }
    /// <summary>Takes control of the seizer.</summary>
    void TakeControl(TNpc npc);
    /// <summary>The controlled enemy tries to take hold.</summary>
    void Grab();
    bool IsHolding { get; }
}

/// <summary>
/// The Warring States corpse seizure (with Retreat): once the lord falls, half the living enemies go
/// for the body and carry it off, the faster the more they outnumber the retainers standing over it,
/// and not at all while those are at least as many. Moved from <c>Plugin.War.cs</c> (refactor stage B3);
/// its battle rule (<c>Rules.CorpseSeizureRule</c>) calls <see cref="Tick"/> in the plugin's war tick (stage B6),
/// the plugin calls <see cref="Reset"/> where <c>ResetWar</c> reset it and <see cref="NoteHit"/> for every hit
/// that lands (<c>CombatEvents.DamageLanded</c>). The rule by numbers and the knock-back on a hit date from
/// 2026-11-15 (docs/WAR_GENERATOR.md §4.3).
/// </summary>
public sealed class CorpseSeizure<TNpc> where TNpc : class
{
    // ---- The seizing's numbers (tuned with Tests/TargetingSim, scenario Seize; see its README) ----
    // Half the living enemies (the nearest to the body, at least one) go for the lord's body; the rest
    // fight on. Seizers whose edge is within the contest radius are counted against the retainers within
    // it. While some seizer is within reach of the body (its edge within SeizeReach of it) and the
    // seizers there outnumber the retainers there, the seizing fills by (seizers - retainers) /
    // SeizeSecondsPerMargin of the whole per second: one seizer alone takes the whole of it, five against
    // four likewise, five against two a third of it. Otherwise it drains by SeizeDrainPerSecond of the
    // whole per second: a shove in the crush does not start it over. Every hit that lands on a seizer
    // within the contest radius knocks it back by SeizeHitKnockback (not below 0). Until the lord's
    // ragdoll is up it stops at SeizeCapBeforeBody. Full: the body is carried off.
    private const float SeizeReach = 1.5f;
    private const float SeizeSecondsPerMargin = 4f;
    private const float SeizeDrainPerSecond = 0.25f;
    private const float SeizeHitKnockback = 0.1f;
    private const float SeizeCapBeforeBody = 0.95f;
    private const float SeizerShare = 0.5f;
    private const float SeizeReportSeconds = 2f;
    // Every few seconds a seizer much further from the body than an enemy who is not one goes back to
    // the fight (and the nearer one takes its place); sentries that never noticed the fight and stand
    // further than this are not woken for it.
    private const float SeizerReviewSeconds = 3f;
    private const float SeizerKeepDistance = 10f;
    private const float SeizerWakeRange = 40f;
    private float seizerReviewTimer;
    // The grab on the seized body is tried again until it holds (it cannot while the lord's ragdoll is
    // still waiting to start, or a hand finds no bone yet).
    private const float GrabRetryInterval = 0.4f;
    private const float GrabRetrySeconds = 8f;
    private float grabRetryTimer;
    private float grabRetryLeft;

    private float seizeProgress;
    private float seizeReportTimer;
    private int hitsSinceReport;
    private readonly List<TNpc> bodySeizers = new();
    private readonly List<(TNpc Npc, float Distance)> seizerCandidates = new();
    private bool bodyContested;
    private bool bodySeized;

    private readonly BattleRules rules;
    private readonly ICorpseSeizureWorld<TNpc> world;
    private readonly ICorpseGrab<TNpc> grab;
    private readonly float contestRadius;

    /// <param name="contestRadius">
    /// Retainers and seizers within this of the body are counted against each other
    /// (<c>CombatCompanionManager.LordContestRadius</c>).
    /// </param>
    public CorpseSeizure(BattleRules rules, ICorpseSeizureWorld<TNpc> world, ICorpseGrab<TNpc> grab, float contestRadius)
    {
        this.rules = rules;
        this.world = world;
        this.grab = grab;
        this.contestRadius = contestRadius;
    }

    /// <summary>The lord's body has been carried off.</summary>
    public bool BodySeized => bodySeized;

    /// <summary>The seizing so far, 0..1 (full: carried off). Shown above the body while above 0.</summary>
    public float Progress => seizeProgress;

    /// <summary>
    /// A hit landed on someone (<paramref name="targetId"/>): one on a seizer within the contest radius of the body
    /// knocks the seizing back. Anything else, or with nothing at stake, does nothing.
    /// </summary>
    public void NoteHit(uint targetId)
    {
        if (!LordBodyAtStake || world.OutcomeDecided || bodySeizers.Count == 0)
            return;
        TNpc? seizer = null;
        foreach (var npc in bodySeizers)
            if (world.IdOf(npc) == targetId)
            {
                seizer = npc;
                break;
            }
        if (seizer == null || world.LordBodyLocation() is not { } body)
            return;
        var at = world.PositionOf(seizer);
        if (new Vector2(at.X - body.X, at.Z - body.Z).Length() - world.HitboxRadiusOf(seizer) > contestRadius)
            return;
        hitsSinceReport++;
        seizeProgress = MathF.Max(0f, seizeProgress - SeizeHitKnockback);
    }

    /// <summary>Whether the lord's body is to be guarded (Warring States with Retreat, the lord down).</summary>
    private bool LordBodyAtStake => rules.LordBodyIsGuarded && !bodySeized;

    /// <summary>Called on every reset or stop of the battle.</summary>
    public void Reset()
    {
        seizeProgress = 0f;
        hitsSinceReport = 0;
        grabRetryLeft = 0f;
        ReleaseSeizers();
        bodySeized = false;
        world.SetCorpseCaptured(false);
    }

    private void ReleaseSeizers()
    {
        foreach (var npc in bodySeizers)
        {
            world.SetSeizeGoal(npc, null);
            world.SetSeizeHeld(npc, false);
        }
        bodySeizers.Clear();
        world.ClearSeizerIds();
        bodyContested = false;
    }

    /// <summary>Once per frame while a battle is under way, after the engine and before the enemy AI.</summary>
    public void Tick(float deltaTime)
    {
        TickGrabRetry(deltaTime);
        if (!LordBodyAtStake || world.OutcomeDecided)
        {
            if (bodySeizers.Count > 0)
                ReleaseSeizers();
            seizeProgress = 0f;
            return;
        }

        if (world.LordBodyLocation() is not { } body)
            return;

        // Who goes for the body: those already going (while they live), then the nearest others,
        // until half the living enemies are on it.
        bodySeizers.RemoveAll(npc =>
        {
            if (world.IsPresent(npc))
                return false;
            world.SetSeizeGoal(npc, null);
            world.SetSeizeHeld(npc, false);
            return true;
        });
        seizerCandidates.Clear();
        var living = 0;
        foreach (var npc in world.Enemies)
        {
            if (!world.IsPresent(npc) || world.IsCaptive(npc))
                continue;
            living++;
            if (bodySeizers.Contains(npc))
                continue;
            var pos = world.PositionOf(npc);
            var distance = new Vector2(pos.X - body.X, pos.Z - body.Z).Length();
            if (world.IsIdleSentry(npc) && distance > SeizerWakeRange)
                continue;
            seizerCandidates.Add((npc, distance));
        }
        seizerCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        seizerReviewTimer -= deltaTime;
        if (seizerReviewTimer <= 0f)
        {
            seizerReviewTimer = SeizerReviewSeconds;
            if (seizerCandidates.Count > 0)
            {
                var keep = MathF.Max(SeizerKeepDistance, seizerCandidates[0].Distance * 2f);
                bodySeizers.RemoveAll(npc =>
                {
                    var pos = world.PositionOf(npc);
                    if (new Vector2(pos.X - body.X, pos.Z - body.Z).Length() <= keep)
                        return false;
                    world.SetSeizeGoal(npc, null);
                    world.SetSeizeHeld(npc, false);
                    return true;
                });
            }
        }
        var wanted = living == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(living * SeizerShare));
        if (bodySeizers.Count < wanted)
        {
            for (var i = 0; i < seizerCandidates.Count && bodySeizers.Count < wanted; i++)
            {
                var npc = seizerCandidates[i].Npc;
                bodySeizers.Add(npc);
                // Woken if it was standing idle (a sentry): the body is what it is here for.
                world.Engage(npc);
            }
        }
        world.ClearSeizerIds();
        foreach (var npc in bodySeizers)
        {
            world.SetSeizeGoal(npc, body);
            world.AddSeizerId(world.IdOf(npc));
        }

        // The body is held while the retainers standing over it are at least as many as the seizers
        // there: outnumber them and it can be taken, without first cutting down every last guard.
        var radius = contestRadius;
        var guardsAtBody = world.RetainersAtBody(body, radius, out var guardName, out var guardDistance);
        var seizersAtBody = 0;
        foreach (var npc in bodySeizers)
        {
            var at = world.PositionOf(npc);
            if (new Vector2(at.X - body.X, at.Z - body.Z).Length() - world.HitboxRadiusOf(npc) <= radius)
                seizersAtBody++;
        }
        var contested = guardsAtBody > 0 && guardsAtBody >= seizersAtBody;
        foreach (var npc in bodySeizers)
            world.SetSeizeHeld(npc, contested);
        if (contested != bodyContested)
        {
            bodyContested = contested;
            if (!contested && bodySeizers.Count > 0)
                world.BattleLog("Nobody stands over the lord's body!");
        }

        TNpc? closest = null;
        var closestGap = float.MaxValue;
        foreach (var npc in bodySeizers)
        {
            var pos = world.PositionOf(npc);
            var gap = new Vector2(pos.X - body.X, pos.Z - body.Z).Length() - world.HitboxRadiusOf(npc);
            if (gap < closestGap)
            {
                closestGap = gap;
                closest = npc;
            }
        }

        // Filling: someone at the body and the seizers there outnumber the retainers there (then not held off,
        // and the margin is at least one), by the margin.
        var atBody = closest != null && closestGap <= SeizeReach;
        var margin = seizersAtBody - guardsAtBody;
        seizeProgress = atBody && !contested && margin > 0
            ? seizeProgress + deltaTime * margin / SeizeSecondsPerMargin
            : MathF.Max(0f, seizeProgress - deltaTime * SeizeDrainPerSecond);
        // The body is taken in hand: not before the lord's ragdoll is there to be held.
        var bodyReady = world.BodyReady;
        if (!bodyReady)
            seizeProgress = MathF.Min(seizeProgress, SeizeCapBeforeBody);

        seizeReportTimer -= deltaTime;
        if (seizeReportTimer <= 0f)
        {
            seizeReportTimer = SeizeReportSeconds;
            world.Info($"War: lord's body at {body:F1}; {bodySeizers.Count} going for it, the closest {(closest == null ? "none" : $"{world.NameOf(closest)} {closestGap:F1} m from it")}; " +
                       $"{seizersAtBody} seizers and {guardsAtBody} retainers within {radius:0.#} m; " +
                       (contested ? $"held off, nearest retainer {guardName} {guardDistance:F1} m from it" : guardName == null ? "no retainer left" : $"not held, nearest retainer {guardName} {guardDistance:F1} m away") +
                       (bodyReady ? "" : "; body not yet fallen (ragdoll waiting)") +
                       $"; seized {seizeProgress:P0}" +
                       (hitsSinceReport > 0 ? $", knocked back by {hitsSinceReport} hits" : "") + ".");
            hitsSinceReport = 0;
        }

        if (closest != null && seizeProgress >= 1f)
            SeizeBody(closest);
    }

    private void SeizeBody(TNpc npc)
    {
        bodySeized = true;
        world.SetCorpseCaptured(true);
        ReleaseSeizers();
        world.BattleLog($"{world.NameOf(npc)} carries off the lord's body!");
        world.SurrenderRetainers();
        world.ForceEnemyVictory();

        // Enemy Control's grab, with its current settings, on the body.
        if (grab.TakesOverSeizer && grab.BodyCanBeHeld && !grab.IsControlling)
        {
            grab.TakeControl(npc);
            if (grab.IsControlling)
            {
                grab.Grab();
                if (!grab.IsHolding)
                {
                    grabRetryLeft = GrabRetrySeconds;
                    grabRetryTimer = GrabRetryInterval;
                }
            }
        }
    }

    /// <summary>A grab on the seized body that did not take is tried again for a few seconds.</summary>
    private void TickGrabRetry(float deltaTime)
    {
        if (grabRetryLeft <= 0f)
            return;
        if (!grab.IsControlling || grab.IsHolding)
        {
            if (grab.IsHolding)
                world.Info("War: the seizer has the lord's body in hand.");
            grabRetryLeft = 0f;
            return;
        }
        grabRetryLeft -= deltaTime;
        grabRetryTimer -= deltaTime;
        if (grabRetryTimer > 0f)
            return;
        grabRetryTimer = GrabRetryInterval;
        grab.Grab();
        if (grabRetryLeft <= 0f && !grab.IsHolding)
            world.Warning("War: the seizer could not take hold of the lord's body.");
    }
}
