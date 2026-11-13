// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Numerics;
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

    private uint prayerId;
    private bool prayerAtPost;
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
    private bool TickPrayer(CombatCompanion companion, float deltaTime, TerrainHeightCache? terrainCache)
    {
        if (prayerId == 0 || companion.SimulatedEntityId != prayerId || !TryGetLordBody(out var body, out var post))
            return false;

        companion.State.IncomingDamageScale = PrayerDamageScale;
        var self = (Vector3)((GameObject*)companion.BattleChara)->Position;
        if (!prayerAtPost && FlatDistance(self, post) > PrayerReach)
        {
            EnterCompanionState(companion, CompanionAiState.MovingToTarget, deltaTime);
            MoveToward(companion, post, deltaTime, terrainCache);
            return true;
        }

        StopMove(companion);
        RotateToward(companion, body, deltaTime);
        if (!prayerAtPost)
        {
            prayerAtPost = true;
            animationController.PlayEmoteOn((Character*)companion.BattleChara, PrayEmoteId);
        }
        return true;
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
