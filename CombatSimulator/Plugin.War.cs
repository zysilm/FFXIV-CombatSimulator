// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Battle;
using CombatSimulator.Gui;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator;

/// <summary>
/// The War Generator's outcome: the result banner, and under Warring States with Retreat, the lord's
/// body: guarded until the enemy is driven off ("body recovered"), or carried off by an enemy that
/// reaches it (Enemy Control's grab, with its current settings), which loses the battle. The fight
/// over the body is <see cref="CorpseSeizure{TNpc}"/>; this file gives it the game.
/// </summary>
public sealed unsafe partial class CombatSimulatorPlugin
{
    private readonly WarBanner warBanner = new();
    private CorpseSeizure<SimulatedNpc> corpseSeizure = null!;
    private bool outcomeShown;

    private void WireWar()
    {
        var world = new CorpseSeizureWorld(this);
        corpseSeizure = new CorpseSeizure<SimulatedNpc>(combatEngine.Rules, world, world, Companions.CombatCompanionManager.LordContestRadius);
        var previous = combatEngine.OnOutcomeDecided;
        combatEngine.OnOutcomeDecided = playerWon =>
        {
            previous?.Invoke(playerWon);
            ShowOutcome(playerWon);
        };
        combatEngine.OnSimulationReset += ResetWar;
    }

    private void ShowOutcome(bool playerWon)
    {
        // The first word stands: a battle lost when the body was carried off is not won later.
        if (outcomeShown)
            return;
        outcomeShown = true;

        if (!config.ShowResultBanner)
            return;
        var (title, reason) = DescribeOutcome(playerWon);
        warBanner.Show(title, reason, playerWon ? new Vector4(0.95f, 0.85f, 0.3f, 1f) : new Vector4(0.9f, 0.25f, 0.2f, 1f));
    }

    /// <summary>
    /// The result and why, for the banner. Each battle rule that ends a fight its own way adds its case
    /// here.
    /// </summary>
    private (string Title, string Reason) DescribeOutcome(bool playerWon)
    {
        var lordAlive = combatEngine.State.PlayerState.IsAlive;
        var warringStates = combatEngine.Rules.PlayerIsTheLord;
        var anyExecuted = combatEngine.State.PlayerState.Executed;
        foreach (var companion in companionManager.Companions)
            if (companion.State.Executed)
                anyExecuted = true;

        if (!playerWon)
        {
            if (corpseSeizure.BodySeized)
                return ("DISGRACED", "Your corpse has been dragged off as a trophy. Your army is executed.");
            if (anyExecuted)
                return ("ROUTED", warringStates
                    ? "You have fallen and your captured soldiers have been executed."
                    : "Your party has been executed.");
            return ("ANNIHILATED", "Every member of your party lies dead on the field.");
        }

        if (!lordAlive && warringStates)
            return ("REMAINS SECURED", "You fell, but your remains are secured and will be carried back to Eorzea.");
        if (!lordAlive)
            return ("VICTORY", "Every enemy has been slain, though you fell in the battle.");
        return ("VICTORY", "Every enemy has been slain. Your army is victorious.");
    }

    private void ResetWar()
    {
        corpseSeizure.Reset();
        outcomeShown = false;
        warBanner.Hide();
    }

    private void TickWar(float deltaTime) => corpseSeizure.Tick(deltaTime);

    /// <summary>Where a fallen actor's body lies: its ragdoll's pelvis, or null without one.</summary>
    private Vector3? BodyPositionOf(nint address)
    {
        if (!config.EnableRagdoll || address == nint.Zero)
            return null;
        if (npcRagdolls.TryGetValue(address, out var npc))
            return npc.IsActive ? npc.GetBodyWorldPosition("j_kosi") : null;
        return Core.Services.ObjectTable.LocalPlayer?.Address == address ? LordBodyPosition() : null;
    }

    /// <summary>Where the lord's body lies: its ragdoll pelvis once it falls, else where the lord died.</summary>
    private Vector3? LordBodyPosition()
        => config.EnableRagdoll && ragdollController.IsActive ? ragdollController.GetBodyWorldPosition("j_kosi") : null;

    private void DrawWar() => warBanner.Draw();

    /// <summary>
    /// The game behind the corpse seizure: the plugin's fields are read when asked, as the code did when
    /// it lived here.
    /// </summary>
    private sealed class CorpseSeizureWorld : ICorpseSeizureWorld<SimulatedNpc>, ICorpseGrab<SimulatedNpc>
    {
        private readonly CombatSimulatorPlugin plugin;

        public CorpseSeizureWorld(CombatSimulatorPlugin plugin) => this.plugin = plugin;

        public bool OutcomeDecided => plugin.outcomeShown;

        public Vector3? LordBodyLocation()
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null)
                return null;
            return plugin.LordBodyPosition() ?? player.Position;
        }

        public IReadOnlyList<SimulatedNpc> Enemies => plugin.npcSelector.SelectedNpcs;
        public bool IsPresent(SimulatedNpc npc) => npc.IsSpawned && npc.IsAlive && npc.BattleChara != null;
        public bool IsCaptive(SimulatedNpc npc) => npc.State.IsCaptive;
        public bool IsIdleSentry(SimulatedNpc npc) => npc.IsSentry && npc.AiState == Ai.NpcAiState.Idle;
        public Vector3 PositionOf(SimulatedNpc npc) => (Vector3)((GameObject*)npc.BattleChara)->Position;
        public float HitboxRadiusOf(SimulatedNpc npc) => npc.HitboxRadius;
        public string NameOf(SimulatedNpc npc) => npc.Name;
        public uint IdOf(SimulatedNpc npc) => npc.SimulatedEntityId;
        public void SetSeizeGoal(SimulatedNpc npc, Vector3? goal) => npc.SeizeGoal = goal;
        public void SetSeizeHeld(SimulatedNpc npc, bool held) => npc.SeizeHeld = held;
        public void Engage(SimulatedNpc npc) => plugin.npcAiController.EngageNpc(npc);
        public void ClearSeizerIds() => plugin.companionManager.LordSeizers.Clear();
        public void AddSeizerId(uint id) => plugin.companionManager.LordSeizers.Add(id);

        public int RetainersAtBody(Vector3 body, float radius, out string? nearestGuard, out float nearestDistance)
            => plugin.companionManager.RetainersAtBody(body, radius, out nearestGuard, out nearestDistance);

        public bool BodyReady => !plugin.config.EnableRagdoll || plugin.ragdollController.IsActive;
        public void SetCorpseCaptured(bool captured) => plugin.companionManager.LordCorpseCaptured = captured;
        public void SurrenderRetainers() => plugin.companionManager.SurrenderRetainers();
        public void ForceEnemyVictory() => plugin.combatEngine.ForceEnemyVictory();
        public void BattleLog(string text) => plugin.combatEngine.AddLogEntry(text, CombatLogType.Death);
        public void Info(string text) => plugin.log.Info(text);
        public void Warning(string text) => plugin.log.Warning(text);

        public bool BodyCanBeHeld => plugin.config.EnableRagdoll && plugin.ragdollController.IsActive;
        public bool IsControlling => plugin.enemyControlController.IsActive;
        public void TakeControl(SimulatedNpc npc) => plugin.enemyControlController.ControlKiller(npc);
        public void Grab() => plugin.enemyControlController.OnAttackInput();
        public bool IsHolding => plugin.enemyControlController.IsHoldingGrab;
    }
}
