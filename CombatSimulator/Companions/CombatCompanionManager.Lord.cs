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
    private const float SeizerTargetBonus = 400f;
    private const float NearBodyTargetBonus = 200f;
    private const float NearBodyTargetReach = 12f;

    /// <summary>The enemies sent to carry off the lord's body (set by the battle each frame).</summary>
    public HashSet<uint> LordSeizers { get; } = new();

    private uint prayerId;
    private bool prayerAtPost;
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
        body = player.Position;
        var facing = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
        post = body + facing * PrayerStandOff;
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
            return;

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
        ClearCompanionActionState(nearest);
        ClearCompanionTargetState(nearest);
        combatEngine.AddLogEntry($"{nearest.Name} kneels to pray over the lord.", CombatLogType.Info);
    }

    /// <summary>
    /// Drive the companion praying over the lord: to its post before the body, then praying there,
    /// taking <see cref="PrayerDamageScale"/> times the damage. True when this is that companion.
    /// </summary>
    private bool TickPrayer(CombatCompanion companion, float deltaTime)
    {
        if (prayerId == 0 || companion.SimulatedEntityId != prayerId || !TryGetLordBody(out var body, out var post))
            return false;

        companion.State.IncomingDamageScale = PrayerDamageScale;
        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;
        if (!prayerAtPost && FlatDistance(self, post) > PrayerReach)
        {
            EnterCompanionState(companion, CompanionAiState.MovingToTarget, deltaTime);
            MoveToward(companion, post, deltaTime);
            return true;
        }

        HaltForEmote(companion);
        RotateToward(companion, body, deltaTime);
        if (!prayerAtPost)
        {
            prayerAtPost = true;
            prayerLoop = animationController.PlayEmoteOn((Character*)companion.BattleChara, PrayEmoteId)
                ? animationController.EmoteLoopTimeline(PrayEmoteId)
                : (ushort)0;
        }
        AnimationController.HoldEmoteLoop((Character*)companion.BattleChara, prayerLoop);
        return true;
    }

    /// <summary>
    /// Whether a retainer who can still fight for it (alive, not captive) stands within
    /// <paramref name=radius/> of the lord's body: while one does, nobody can carry it off.
    /// </summary>
    public bool LordBodyContested(Vector3 body, float radius)
    {
        foreach (var companion in companions)
        {
            if (!CanRescue(companion))
                continue;
            if (FlatDistance((Vector3)((GameObject*)companion.BattleChara)->Position, body) <= radius)
                return true;
        }
        return false;
    }

    /// <summary>Guarding the body: the enemies going for it first, then those nearest it.</summary>
    private float LordGuardScore(Npcs.SimulatedNpc enemy, float distanceToBody)
    {
        if (!GuardingLord)
            return 0f;
        var score = 0f;
        if (LordSeizers.Contains(enemy.SimulatedEntityId))
            score += SeizerTargetBonus;
        if (float.IsFinite(distanceToBody))
            score += NearBodyTargetBonus * Math.Clamp(1f - distanceToBody / NearBodyTargetReach, 0f, 1f);
        return score;
    }

    /// <summary>
    /// Enemies going for the lord's body fight whoever stands over it: the one praying most of all (it
    /// cannot fight back), then the guards nearest the body.
    /// </summary>
    private float LordSeizerTargetScore(uint enemyId, uint candidateId)
    {
        if (!GuardingLord || !LordSeizers.Contains(enemyId) || !TryGetLordBody(out var body, out _))
            return 0f;
        var guard = GetCompanion(candidateId);
        if (guard == null || guard.BattleChara == null)
            return 0f;
        var d = FlatDistance((Vector3)((GameObject*)guard.BattleChara)->Position, body);
        var score = SeizerTargetBonus * Math.Clamp(1f - d / NearBodyTargetReach, 0f, 1f);
        if (candidateId == prayerId)
            score += SeizerTargetBonus;
        return score;
    }

    /// <summary>A guard's place in the ring round the lord's body.</summary>
    private static Vector3 GuardRingPosition(Vector3 body, int index, int count)
    {
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
