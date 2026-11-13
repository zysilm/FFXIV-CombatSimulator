// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Companions;

/// <summary>
/// The Warring States rule: the player is the lord. When the lord falls, the retainers are taken
/// captive; or, retreating, one of them goes to stand before the body and prays (it cannot fight,
/// and takes four times the damage) while the others guard it, the next taking its place if it falls.
/// </summary>
public unsafe partial class CombatCompanionManager
{
    public const float PrayerDamageScale = 4f;
    private const uint PrayEmoteId = 58;
    private const float PrayerStandOff = 1.3f;
    private const float PrayerReach = 0.6f;
    // Guarding the body: the allies keep within this of it, and stand in a ring this far out while
    // nobody comes; an enemy inside the contest radius (and not yet dead) is fought before any other.
    private const float LordGuardRange = 10f;
    private const float GuardRingRadius = 3f;
    public const float LordContestRadius = 3.5f;
    private const float NearBodyTargetReach = 12f;

    /// <summary>Where the lord's body actually lies (the ragdoll, once it falls); null: where the lord died.</summary>
    public Func<Vector3?>? LordBodyPosition { private get; set; }

    private Vector3 LordBodyAt(Vector3 playerPosition) => LordBodyPosition?.Invoke() ?? playerPosition;

    /// <summary>The enemies sent to carry off the lord's body (set by the battle each frame).</summary>
    public HashSet<uint> LordSeizers { get; } = new();

    // Pushed further than this from where it knelt, the one praying gets up and goes back; one that
    // cannot get nearer to its post for this long kneels where it is.
    private const float PrayerPushedOff = 1.0f;
    private const float PrayerStallSeconds = 1.5f;

    private uint prayerId;
    private bool prayerAtPost;
    private Vector3 prayerSpot;
    private float prayerBest = float.MaxValue;
    private float prayerStall;
    private ushort prayerLoop;
    private bool lordFallHandled;

    private bool WarringStates => config.WarFallenRule == (int)WarFallenRule.WarringStates;

    /// <summary>Whether the lord has fallen and the retainers are retreating with the body.</summary>
    public bool GuardingLord
        => WarringStates && config.WarRetreat && combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;

    /// <summary>Whether a retainer is left who can stand before the lord's body (alive, not captive).</summary>
    public bool HasRetainer
    {
        get
        {
            foreach (var companion in companions)
                if (CanRescue(companion))
                    return true;
            return false;
        }
    }

    /// <summary>The companion praying over the lord's body, if any.</summary>
    public uint PrayerId => prayerId;

    /// <summary>Where the lord's body lies, and where the one praying stands: before it.</summary>
    private bool TryGetLordBody(out Vector3 body, out Vector3 post)
    {
        body = post = default;
        var player = objectTable.LocalPlayer;
        if (player == null)
            return false;
        body = LordBodyAt(player.Position);
        var facing = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
        post = body + facing * PrayerStandOff;
        if (Crowd?.TrySnapToFloor(post, out var floor) == true)
            post = floor;
        return true;
    }

    private void TickLord()
    {
        var lordDown = WarringStates && combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;
        if (!lordDown)
        {
            lordFallHandled = false;
            ReleasePrayer();
            return;
        }

        if (!lordFallHandled)
        {
            lordFallHandled = true;
            if (!config.WarRetreat)
            {
                // No retreat: every retainer left is taken, to be executed.
                foreach (var companion in companions)
                    if (CanRescue(companion))
                        combatEngine.TakeCaptive(companion.State);
                return;
            }
            combatEngine.AddLogEntry("The lord has fallen! The retainers fall back to guard the body.", CombatLogType.Death);
        }

        if (!config.WarRetreat)
        {
            ReleasePrayer();
            return;
        }

        if (prayerId != 0 && (GetCompanion(prayerId) is not { } current || !CanRescue(current)))
            ReleasePrayer();
        if (prayerId != 0 || !TryGetLordBody(out _, out var post))
            return;

        CombatCompanion? nearest = null;
        var best = float.MaxValue;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion))
                continue;
            var d = FlatDistance((Vector3)((GameObject*)companion.BattleChara)->Position, post);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        if (nearest == null)
            return;

        prayerId = nearest.SimulatedEntityId;
        prayerAtPost = false;
        prayerBest = float.MaxValue;
        prayerStall = 0f;
        ClearCompanionActionState(nearest);
        ClearCompanionTargetState(nearest);
        combatEngine.AddLogEntry($"{nearest.Name} goes to the lord's body.", CombatLogType.Info);
    }

    /// <summary>
    /// Drive the companion praying over the lord: to its post before the body, then praying there,
    /// taking <see cref="PrayerDamageScale"/> times the damage. True when this is that companion.
    /// </summary>
    private bool TickPrayer(CombatCompanion companion, float deltaTime)
    {
        if (prayerId == 0 || companion.SimulatedEntityId != prayerId || !TryGetLordBody(out var body, out var post))
            return false;

        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;

        // Pushed off where it knelt: up, and back to the post.
        if (prayerAtPost && FlatDistance(self, prayerSpot) > PrayerPushedOff)
        {
            prayerAtPost = false;
            prayerBest = float.MaxValue;
            prayerStall = 0f;
            companion.State.IncomingDamageScale = 1f;
            animationController.EmotePlayer.ResetEmote((Character*)companion.BattleChara);
        }

        if (!prayerAtPost)
        {
            var off = FlatDistance(self, post);
            if (off < prayerBest - 0.1f)
            {
                prayerBest = off;
                prayerStall = 0f;
            }
            else
                prayerStall += deltaTime;
            if (off > PrayerReach && prayerStall < PrayerStallSeconds)
            {
                EnterCompanionState(companion, CompanionAiState.MovingToTarget, deltaTime);
                MoveToward(companion, post, deltaTime);
                return true;
            }
        }

        HaltForEmote(companion);
        RotateToward(companion, body, deltaTime);
        if (!prayerAtPost)
        {
            // Only kneeling does it lay itself open.
            prayerAtPost = true;
            prayerSpot = self;
            companion.State.IncomingDamageScale = PrayerDamageScale;
            prayerLoop = animationController.PlayEmoteOn((Character*)companion.BattleChara, PrayEmoteId)
                ? animationController.EmoteLoopTimeline(PrayEmoteId)
                : (ushort)0;
            combatEngine.AddLogEntry($"{companion.Name} kneels to pray over the lord.", CombatLogType.Info);
        }
        AnimationController.HoldEmoteLoop((Character*)companion.BattleChara, prayerLoop);
        return true;
    }

    /// <summary>
    /// Whether a retainer who can still fight for it (alive, not captive) stands within
    /// <paramref name=radius/> of the lord's body: while one does, nobody can carry it off.
    /// </summary>
    public bool LordBodyContested(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
    {
        nearestGuard = null;
        nearestDistance = float.MaxValue;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion))
                continue;
            var d = FlatDistance((Vector3)((GameObject*)companion.BattleChara)->Position, body);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearestGuard = companion.Name;
            }
        }
        return nearestDistance <= radius;
    }

    /// <summary>
    /// The one an enemy going for the lord's body must cut down: the one praying over it (it cannot
    /// fight back), else the retainer nearest the body. Null for any other enemy, or when no retainer
    /// is left. The ordinary target choice spreads enemies over targets, which kept a crowd of seizers
    /// off the one retainer holding the body for good.
    /// </summary>
    private CombatCompanion? LordSeizerTarget(Npcs.SimulatedNpc enemy)
    {
        if (!GuardingLord || !LordSeizers.Contains(enemy.SimulatedEntityId) || !TryGetLordBody(out var body, out _))
            return null;
        // Only those at the body stand in a seizer's way; a retainer far off is left to the ordinary
        // choice (or a ranged seizer would stop to shoot it while the body lay open).
        const float atBody = LordContestRadius + 2f;
        if (prayerId != 0 && GetCompanion(prayerId) is { } prayer && CanRescue(prayer) &&
            FlatDistance((Vector3)((GameObject*)prayer.BattleChara)->Position, body) <= atBody)
            return prayer;
        CombatCompanion? nearest = null;
        var best = atBody;
        foreach (var companion in companions)
        {
            if (!CanRescue(companion))
                continue;
            var d = FlatDistance((Vector3)((GameObject*)companion.BattleChara)->Position, body);
            if (d < best)
            {
                best = d;
                nearest = companion;
            }
        }
        return nearest;
    }

    // A guard that strays past its leash walks back until it is this share of the way in, so it does
    // not flicker in and out at the edge.
    private const float GuardReturnShare = 0.7f;

    /// <summary>
    /// Whether a guard of the lord's body is inside its leash: past the leash it must come back, and
    /// stays coming back until well inside.
    /// </summary>
    private bool GuardInsideLeash(CombatCompanion companion, float distance, float limit)
    {
        if (companion.ReturningToLord)
        {
            if (distance <= limit * GuardReturnShare)
                companion.ReturningToLord = false;
        }
        else if (distance > limit)
            companion.ReturningToLord = true;
        return !companion.ReturningToLord;
    }

    /// <summary>
    /// Where a guard goes when it is not fighting: coming back from past its leash, well inside it on
    /// its own side; otherwise its place in the ring of guards (the one praying and those raising
    /// are not in the ring).
    /// </summary>
    private Vector3 GuardGoal(CombatCompanion companion, Vector3 body, Vector3 current)
    {
        if (companion.ReturningToLord)
        {
            var away = new Vector3(current.X - body.X, 0f, current.Z - body.Z);
            away = away.LengthSquared() > 1e-4f ? Vector3.Normalize(away) : Vector3.UnitZ;
            return body + away * (CommandLimit(companion.SimulatedEntityId) * GuardReturnShare * 0.85f);
        }
        int index = 0, count = 0;
        foreach (var other in companions)
        {
            if (!CanRescue(other) || other.SimulatedEntityId == prayerId || IsRescuing(other.SimulatedEntityId))
                continue;
            if (other == companion)
                index = count;
            count++;
        }
        var angle = MathF.Tau * index / Math.Max(1, count);
        return body + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * GuardRingRadius;
    }

    private void ReleasePrayer()
    {
        if (prayerId == 0)
            return;
        if (GetCompanion(prayerId) is { } prayer)
        {
            prayer.State.IncomingDamageScale = 1f;
            if (prayerAtPost && prayer.BattleChara != null && prayer.State.IsAlive && !prayer.State.IsCaptive)
                animationController.EmotePlayer.ResetEmote((Character*)prayer.BattleChara);
        }
        prayerId = 0;
        prayerAtPost = false;
        prayerBest = float.MaxValue;
        prayerStall = 0f;
    }

    /// <summary>The lord's body has been carried off: whoever is left is taken.</summary>
    public void SurrenderRetainers()
    {
        ReleasePrayer();
        foreach (var companion in companions)
            if (CanRescue(companion))
                combatEngine.TakeCaptive(companion.State);
    }

    /// <summary>What one of the player's side is doing, for the HP bars.</summary>
    public string StatusOf(SimulatedEntityState state)
    {
        if (!state.IsAlive)
            return "Fallen";
        if (state.IsCaptive)
            return "Captive";
        if (state.IsPlayer)
            return "Fighting";
        if (state.EntityId == prayerId && prayerId != 0)
            return "Praying";
        if (IsRescuing(state.EntityId))
            return "Raising";
        if (GuardingLord)
            return "Guarding";
        return "Fighting";
    }

    private void ResetLord()
    {
        ReleasePrayer();
        lordFallHandled = false;
    }
}
