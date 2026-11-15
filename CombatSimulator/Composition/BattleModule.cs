// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Battle;
using CombatSimulator.Companions;
using CombatSimulator.Core;
using CombatSimulator.Gui;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Composition;

/// <summary>
/// How a battle ends (docs/PLUGIN_ARCHITECTURE.md §6.1): the victory celebration, the result banner, and
/// under Warring States with Retreat, the lord's body: guarded until the enemy is driven off ("body
/// recovered"), or carried off by an enemy that reaches it (Enemy Control's grab, with its current
/// settings), which loses the battle. The fight over the body is <see cref="CorpseSeizure{TNpc}"/>, run by
/// its battle rule (<see cref="Battle.Rules.CorpseSeizureRule"/>); this module gives it the game. The
/// banner's words are the battle rules' (<see cref="Battle.Rules.IBattleRule.DescribeOutcome"/>).
/// </summary>
internal sealed unsafe class BattleModule
{
    private readonly PluginModules m;

    private VictoryCelebrationDirector? victoryCelebration;
    private readonly WarBanner warBanner = new();
    private CorpseSeizure<SimulatedNpc> corpseSeizure = null!;
    private bool outcomeShown;

    public BattleModule(PluginModules modules) => m = modules;

    public VictoryCelebrationDirector? VictoryCelebration => victoryCelebration;
    public WarBanner Banner => warBanner;

    private Configuration config => m.Config;
    private IPluginLog log => m.Log;
    private AnimationController animationController => m.World.Animation;
    private CombatEngine combatEngine => m.Combat.Engine;
    private NpcAiController npcAiController => m.Combat.EnemyAi;
    private NpcSelector npcSelector => m.Actors.Selector;
    private CombatCompanionManager companionManager => m.Actors.Companions;
    private RagdollController ragdollController => m.Corpse.PlayerRagdoll;
    private EnemyControlController enemyControlController => m.Fighting.EnemyControl;
    private Vector3? LordBodyPosition() => m.Corpse.LordBodyPosition();

    /// <summary>The celebration after an outcome; animation plays it. (The crowd's reaction is subscribed
    /// by the plugin's constructor right after.)</summary>
    public void CreateVictoryCelebration()
    {
        victoryCelebration = new VictoryCelebrationDirector(animationController, Services.DataManager, log)
        {
            MaxStagger = () => config.VictoryCelebrationStagger,
            Companions = LivingCompanionActors,
            IsExternallyDriven = address => enemyControlController.ControlsNpc(address),
            // The celebration lasts exactly as long as the outcome it belongs to: a revive, a new
            // wave or stopping the simulation ends it and returns everyone to idle.
            OutcomeHolds = mood => config.EnableVictoryCelebration && combatEngine.IsActive && mood switch
            {
                CelebrationMood.EnemyVictory => !combatEngine.State.PlayerState.IsAlive && !companionManager.HasLivingCompanions,
                CelebrationMood.PartyVictory => npcSelector.SelectedNpcs.Count > 0 && npcSelector.SelectedNpcs.All(n => !n.IsAlive),
                _ => true,
            },
        };
        animationController.Victory = victoryCelebration;
    }

    private IEnumerable<(nint Address, uint EntityId)> LivingCompanionActors()
    {
        var result = new List<(nint, uint)>();
        foreach (var companion in companionManager.Companions)
            if (companion.IsSpawned && companion.State.IsAlive && companion.BattleChara != null)
                result.Add((companion.Address, companion.SimulatedEntityId));
        return result;
    }

    /// <summary>The fight over the lord's body, given to its battle rule. (The banner and the reset are
    /// subscribed by the plugin's constructor right after.)</summary>
    public void CreateCorpseSeizure()
    {
        var world = new CorpseSeizureWorld(this);
        corpseSeizure = new CorpseSeizure<SimulatedNpc>(combatEngine.Rules, world, world, Companions.CombatCompanionManager.LordContestRadius);
        companionManager.RuleParts.Get<Battle.Rules.CorpseSeizureRule>().Seizure = corpseSeizure;
    }

    public void ShowOutcome(bool playerWon)
    {
        // The first word stands: a battle lost when the body was carried off is not won later.
        if (outcomeShown)
            return;
        outcomeShown = true;

        if (!config.ShowResultBanner)
            return;
        // The words are the battle rules' (each part that ends a fight its own way speaks for it).
        var (title, reason) = companionManager.RuleParts.DescribeOutcome(playerWon);
        warBanner.Show(title, reason, playerWon ? new Vector4(0.95f, 0.85f, 0.3f, 1f) : new Vector4(0.9f, 0.25f, 0.2f, 1f));
    }

    public void ResetWar()
    {
        corpseSeizure.Reset();
        outcomeShown = false;
        warBanner.Hide();
    }

    /// <summary>A hit landed (<c>CombatEvents.DamageLanded</c>): a seizer struck at the lord's body knocks the seizing back.</summary>
    public void NoteDamage(SimulatedEntityState target, DamageResult hit, SimulatedEntityState? source)
    {
        if (!target.IsPlayer && !target.IsCompanion)
            corpseSeizure.NoteHit(target.EntityId);
    }

    /// <summary>The seizing above the lord's body, for the HP bars: where the body lies and how far it has gone
    /// (null while nothing is under way or once the body is carried off).</summary>
    public (Vector3 Body, float Progress)? SeizureBar()
    {
        if (corpseSeizure == null || corpseSeizure.BodySeized || corpseSeizure.Progress <= 0f)
            return null;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return null;
        return (LordBodyPosition() ?? player.Position, corpseSeizure.Progress);
    }

    /// <summary>The battle rules after the engine (the corpse seizure's tick).</summary>
    public void TickWar(float deltaTime) => companionManager.RuleParts.TickAfterEngine(deltaTime);

    public void DrawWar() => warBanner.Draw();

    /// <summary>
    /// The game behind the corpse seizure: the plugin's objects are read when asked, as the code did when
    /// it lived in the plugin class.
    /// </summary>
    private sealed class CorpseSeizureWorld : ICorpseSeizureWorld<SimulatedNpc>, ICorpseGrab<SimulatedNpc>
    {
        private readonly BattleModule plugin;

        public CorpseSeizureWorld(BattleModule plugin) => this.plugin = plugin;

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
        public bool IsIdleSentry(SimulatedNpc npc) => npc.IsSentry && npc.AiState == CombatSimulator.Combat.CombatantAiState.Idle;
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
