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
/// for the body and carry it off unless the retainers standing over it are at least as many. Moved
/// verbatim from <c>Plugin.War.cs</c> (refactor stage B3); the plugin calls <see cref="Tick"/> where it
/// called <c>TickWar</c> and <see cref="Reset"/> where <c>ResetWar</c> reset it.
/// </summary>
public sealed class CorpseSeizure<TNpc> where TNpc : class
{
    // Half the living enemies (the nearest to the body, at least one) go for the lord's body; the rest
    // fight on. The seizing fills while any of them is within reach of the body (its own body's edge
    // within SeizeReach of it) and no retainer stands within the contest radius, and drains at half
    // that rate otherwise: a shove in the crush does not start it over. Full: the body is carried off.
    private const float SeizeReach = 1.5f;
    private const float SeizeSeconds = 2f;
    private const float SeizeDrainRate = 0.5f;
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

    /// <summary>The seizing so far, in seconds (full at 2).</summary>
    public float Progress => seizeProgress;

    /// <summary>Whether the lord's body is to be guarded (Warring States with Retreat, the lord down).</summary>
    private bool LordBodyAtStake => rules.LordBodyIsGuarded && !bodySeized;

    /// <summary>Called on every reset or stop of the battle.</summary>
    public void Reset()
    {
        seizeProgress = 0f;
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

        var atBody = closest != null && closestGap <= SeizeReach;
        seizeProgress = atBody && !contested
            ? seizeProgress + deltaTime
            : MathF.Max(0f, seizeProgress - deltaTime * SeizeDrainRate);
        // The body is taken in hand: not before the lord's ragdoll is there to be held.
        var bodyReady = world.BodyReady;
        if (!bodyReady)
            seizeProgress = MathF.Min(seizeProgress, SeizeSeconds * 0.95f);

        seizeReportTimer -= deltaTime;
        if (seizeReportTimer <= 0f)
        {
            seizeReportTimer = SeizeReportSeconds;
            world.Info($"War: lord's body at {body:F1}; {bodySeizers.Count} going for it, the closest {(closest == null ? "none" : $"{world.NameOf(closest)} {closestGap:F1} m from it")}; " +
                       $"{seizersAtBody} seizers and {guardsAtBody} retainers within {radius:0.#} m; " +
                       (contested ? $"held off, nearest retainer {guardName} {guardDistance:F1} m from it" : guardName == null ? "no retainer left" : $"not held, nearest retainer {guardName} {guardDistance:F1} m away") +
                       (bodyReady ? "" : "; body not yet fallen (ragdoll waiting)") +
                       $"; seized {seizeProgress / SeizeSeconds:P0}.");
        }

        if (closest != null && seizeProgress >= SeizeSeconds)
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
        if (grab.BodyCanBeHeld && !grab.IsControlling)
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
