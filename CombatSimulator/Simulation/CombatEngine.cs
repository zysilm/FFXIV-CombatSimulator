using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CombatSimulator.Simulation;

public class SimulatedActionResult
{
    public bool Success { get; set; }
    public string? FailReason { get; set; }
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public bool IsCombo { get; set; }
    public uint ActionId { get; set; }
    public uint SourceId { get; set; }
    public ulong TargetId { get; set; }
    public bool TargetKilled { get; set; }
}

public class CombatLogEntry
{
    public float Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public CombatLogType Type { get; set; }
}

public enum CombatLogType
{
    DamageDealt,
    DamageTaken,
    Healing,
    Miss,
    Death,
    Info,
}

public class CombatEngine : IDisposable
{
    private readonly ActionDataProvider actionDataProvider;
    private readonly DamageCalculator damageCalculator;
    private readonly AnimationController animationController;
    private readonly GlamourerIpc glamourerIpc;
    private readonly MovementBlockHook movementBlockHook;
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private bool playerDeathTriggered;
    private bool victoryTriggered;
    private bool glamourerApplied;

    public SimulationState State { get; } = new();
    public bool IsActive => State.IsActive;
    public List<CombatLogEntry> CombatLog { get; } = new();

    // Configuration (set from plugin config)
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;

    // Queued player actions (from UseAction hook, processed on framework thread)
    private readonly Queue<QueuedAction> actionQueue = new();
    private readonly object queueLock = new();

    // Pending death animations (delayed so the killing blow visuals play first)
    private readonly List<PendingDeath> pendingDeaths = new();
    private const float DeathAnimationDelay = 0.5f;

    private struct PendingDeath
    {
        public ulong EntityId;
        public bool IsPlayer;
        public float Timer;
    }

    public CombatEngine(
        ActionDataProvider actionDataProvider,
        DamageCalculator damageCalculator,
        AnimationController animationController,
        GlamourerIpc glamourerIpc,
        MovementBlockHook movementBlockHook,
        Configuration config,
        NpcSelector npcSelector,
        IClientState clientState,
        IPluginLog log)
    {
        this.actionDataProvider = actionDataProvider;
        this.damageCalculator = damageCalculator;
        this.animationController = animationController;
        this.glamourerIpc = glamourerIpc;
        this.movementBlockHook = movementBlockHook;
        this.config = config;
        this.npcSelector = npcSelector;
        this.clientState = clientState;
        this.log = log;
    }

    public void StartSimulation()
    {
        if (State.IsActive)
            return;

        InitializePlayerState();
        State.IsActive = true;
        State.SimulationTime = 0;
        State.CombatStartTime = 0;
        playerDeathTriggered = false;
        victoryTriggered = false;
        glamourerApplied = false;
        pendingDeaths.Clear();

        // Apply glamourer combat-ready preset on start
        ApplyResetGlamourer();

        AddLogEntry("Combat simulation started.", CombatLogType.Info);
        log.Info("Combat simulation started.");
    }

    public void StopSimulation()
    {
        if (!State.IsActive)
            return;

        State.IsActive = false;

        lock (queueLock)
            actionQueue.Clear();

        // Clean up all active targets (reset animations, restore ObjectKind, etc.)
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            UnregisterNpcEntity(npc.SimulatedEntityId);
            unsafe
            {
                if (npc.BattleChara != null)
                {
                    animationController.ResetDeathAnimation(npc.BattleChara);
                    animationController.ClearBattleStance(npc);
                    var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)npc.BattleChara;
                    character->Mode = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal;
                    character->ModeParam = 0;
                }
            }
        }
        npcSelector.DeselectAll();
        animationController.ResetPlayerDeathAnimation();
        movementBlockHook.IsBlocking = false;
        RevertGlamourer();

        AddLogEntry("Combat simulation stopped.", CombatLogType.Info);
        log.Info("Combat simulation stopped.");
    }

    public void ResetState()
    {
        State.Reset();
        CombatLog.Clear();

        // Re-initialize player state
        InitializePlayerState();

        // Reset all NPC states
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            npc.State.Reset();
            npc.AiState = Ai.NpcAiState.Idle;
            npc.AutoAttackTimer = 0;
            npc.DeadTimer = 0;
            npc.CurrentCastSkill = null;

            // Revive dead NPCs: reset death animation + restore normal mode
            unsafe
            {
                if (npc.BattleChara != null)
                {
                    animationController.ResetDeathAnimation(npc.BattleChara);
                    var character = (Character*)npc.BattleChara;
                    character->Mode = CharacterModes.Normal;
                    character->ModeParam = 0;
                }
            }
        }

        animationController.ResetPlayerDeathAnimation();
        movementBlockHook.IsBlocking = false;
        RevertGlamourer();
        ApplyResetGlamourer();

        playerDeathTriggered = false;
        victoryTriggered = false;
        glamourerApplied = false;
        pendingDeaths.Clear();

        AddLogEntry("Combat state reset.", CombatLogType.Info);
    }

    public void Tick(float deltaTime)
    {
        if (!State.IsActive)
            return;

        State.SimulationTime += deltaTime;

        // Tick animation cooldowns
        animationController.Tick(deltaTime);

        // Process queued player actions
        ProcessActionQueue();

        // Process pending death animations
        TickPendingDeaths(deltaTime);

        // Tick cooldowns
        TickCooldowns(State.PlayerState, deltaTime);
        foreach (var entity in State.Entities.Values)
            TickCooldowns(entity, deltaTime);

        // Tick animation locks
        TickAnimationLock(State.PlayerState, deltaTime);
        foreach (var entity in State.Entities.Values)
            TickAnimationLock(entity, deltaTime);

        // Tick casting
        TickCasting(State.PlayerState, deltaTime);

        // Tick combo timer
        TickComboTimer(State.PlayerState, deltaTime);

        // Tick status effects
        TickStatusEffects(State.PlayerState, deltaTime);
        foreach (var entity in State.Entities.Values)
            TickStatusEffects(entity, deltaTime);

        // Tick auto-attack
        TickAutoAttack(deltaTime);

        // Tick MP regeneration
        TickMpRegen(State.PlayerState, deltaTime);

    }

    public void EnqueuePlayerAction(uint actionType, uint actionId, ulong targetId, uint extraParam)
    {
        lock (queueLock)
        {
            actionQueue.Enqueue(new QueuedAction
            {
                ActionType = actionType,
                ActionId = actionId,
                TargetId = targetId,
                ExtraParam = extraParam,
            });
        }
    }

    public SimulatedActionResult ProcessPlayerAction(uint actionId, ulong targetId)
    {
        var result = new SimulatedActionResult
        {
            ActionId = actionId,
            SourceId = State.PlayerState.EntityId,
            TargetId = targetId,
        };

        // Validate
        if (!State.PlayerState.IsAlive)
        {
            result.FailReason = "Player is dead.";
            return result;
        }

        var target = State.GetEntity(targetId);
        if (target == null)
        {
            result.FailReason = "Invalid target.";
            return result;
        }

        if (!target.IsAlive)
        {
            result.FailReason = "Target is already dead.";
            return result;
        }

        // Get action data
        var actionData = actionDataProvider.GetActionData(actionId);
        if (actionData == null)
        {
            // Unknown action - use generic values
            actionData = new ActionData
            {
                ActionId = actionId,
                Name = $"Action #{actionId}",
                Potency = 200,
                RecastTime = 2.5f,
                RecastGroup = 58,
                AnimationLock = 0.6f,
                DamageType = SimDamageType.Physical,
            };
        }

        // Check animation lock
        if (State.PlayerState.AnimationLock > 0)
        {
            result.FailReason = "Animation locked.";
            return result;
        }

        // Check GCD
        if (actionData.RecastGroup == 58 && State.PlayerState.GcdRemaining > 0)
        {
            result.FailReason = "GCD not ready.";
            return result;
        }

        // Check cooldown
        if (actionData.RecastGroup != 58 &&
            State.PlayerState.Cooldowns.TryGetValue(actionData.RecastGroup, out var cd) &&
            cd.IsActive)
        {
            result.FailReason = "Action on cooldown.";
            return result;
        }

        // Check combo
        bool isCombo = false;
        if (actionData.IsComboAction && State.PlayerState.ComboTimer > 0 &&
            State.PlayerState.LastComboAction == actionData.ComboFrom)
        {
            isCombo = true;
        }

        // Record combat start
        if (State.CombatStartTime == 0)
            State.CombatStartTime = State.SimulationTime;

        // Calculate damage
        var dmgResult = damageCalculator.Calculate(
            State.PlayerState, target, actionData, isCombo,
            EnableCriticalHits, EnableDirectHits, DamageMultiplier);

        // Apply damage
        target.CurrentHp = Math.Max(0, target.CurrentHp - dmgResult.Damage);

        // Apply animation lock
        State.PlayerState.AnimationLock = actionData.AnimationLock;

        // Apply GCD
        if (actionData.RecastGroup == 58)
            State.PlayerState.GcdRemaining = actionData.RecastTime > 0 ? actionData.RecastTime : 2.5f;

        // Apply cooldown
        if (actionData.RecastGroup != 58 && actionData.RecastTime > 0)
        {
            State.PlayerState.Cooldowns[actionData.RecastGroup] = new RecastState
            {
                ActionId = actionId,
                Elapsed = 0,
                Total = actionData.RecastTime,
            };
        }

        // Update combo
        State.PlayerState.LastComboAction = actionId;
        State.PlayerState.ComboTimer = 30.0f;

        // Update stats
        State.TotalDamageDealt += dmgResult.Damage;

        // Build result
        result.Success = true;
        result.Damage = dmgResult.Damage;
        result.IsCritical = dmgResult.IsCritical;
        result.IsDirectHit = dmgResult.IsDirectHit;
        result.IsCombo = isCombo;

        // Trigger animation
        TriggerActionEffect(State.PlayerState, target, actionData, dmgResult);

        // Log
        var critText = dmgResult.IsCritical ? " critical" : "";
        var dhText = dmgResult.IsDirectHit ? " direct" : "";
        var comboText = isCombo ? " (combo)" : "";
        AddLogEntry(
            $"You use {actionData.Name} → {target.Name}: {dmgResult.Damage:N0}{critText}{dhText} damage{comboText}",
            CombatLogType.DamageDealt);

        // Engage the NPC if it was idle
        Vector3 engagedNpcPos = Vector3.Zero;
        bool didEngage = false;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.SimulatedEntityId == (uint)targetId &&
                npc.AiState == Ai.NpcAiState.Idle)
            {
                npc.AiState = Ai.NpcAiState.Combat;
                AddLogEntry($"{npc.Name} engages!", CombatLogType.Info);
                engagedNpcPos = GetEntityPosition(npc.State);
                didEngage = true;
                break;
            }
        }

        // Aggro propagation: scan the game world for nearby BattleNpcs and add them as targets
        if (didEngage && config.EnableAggroPropagation)
        {
            float aggroRange = config.AggroPropagationRange;

            // First, activate any already-selected idle NPCs in range
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.AiState != Ai.NpcAiState.Idle || !npc.IsSpawned)
                    continue;
                var npcPos = GetEntityPosition(npc.State);
                if (Vector3.Distance(npcPos, engagedNpcPos) <= aggroRange)
                {
                    npc.AiState = Ai.NpcAiState.Combat;
                    AddLogEntry($"{npc.Name} joins the fight!", CombatLogType.Info);
                }
            }

            // Then, scan the object table for new BattleNpcs in range and auto-add them
            var newNpcs = npcSelector.SelectNearbyBattleNpcs(
                engagedNpcPos, aggroRange,
                config.DefaultNpcLevel, config.DefaultNpcHpMultiplier,
                (Npcs.NpcBehaviorType)config.DefaultNpcBehaviorType);

            foreach (var newNpc in newNpcs)
            {
                RegisterNpcEntity(newNpc);
                newNpc.AiState = Ai.NpcAiState.Combat;
                AddLogEntry($"{newNpc.Name} joins the fight!", CombatLogType.Info);
            }
        }

        // Check death
        if (!target.IsAlive)
        {
            result.TargetKilled = true;
            OnEntityDeath(target);
        }

        return result;
    }

    public SimulatedActionResult ProcessNpcAction(SimulatedNpc npc, uint actionId, ulong targetId, int potency = 0)
    {
        var result = new SimulatedActionResult
        {
            ActionId = actionId,
            SourceId = npc.SimulatedEntityId,
            TargetId = targetId,
        };

        var target = State.GetEntity(targetId);
        if (target == null || !target.IsAlive || !npc.State.IsAlive)
        {
            result.FailReason = "Invalid target or source dead.";
            return result;
        }

        // Calculate damage
        DamageResult dmgResult;
        if (potency > 0)
        {
            dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target, potency);
        }
        else
        {
            var actionData = actionDataProvider.GetActionData(actionId);
            if (actionData != null)
            {
                dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target, actionData.Potency);
            }
            else
            {
                dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target);
            }
        }

        // Apply damage to target (player)
        target.CurrentHp = Math.Max(0, target.CurrentHp - dmgResult.Damage);
        State.TotalDamageTaken += dmgResult.Damage;

        // Trigger animation (NPC → Player)
        // Always use ActionId 7 (auto-attack) — player skill ActionIds (31, 141, etc.)
        // animate inconsistently on monster models
        var actionData2 = new ActionData
        {
            ActionId = 7,
            Potency = potency > 0 ? potency : 110,
            DamageType = SimDamageType.Physical,
            AnimationLock = 0.6f,
        };
        TriggerActionEffect(npc.State, target, actionData2, dmgResult);

        result.Success = true;
        result.Damage = dmgResult.Damage;
        result.IsCritical = dmgResult.IsCritical;
        result.IsDirectHit = dmgResult.IsDirectHit;

        // Log
        var actionName = actionId == 7 ? "auto-attacks" : $"uses action";
        AddLogEntry(
            $"{npc.Name} {actionName} → You: {dmgResult.Damage:N0} damage",
            CombatLogType.DamageTaken);

        // Check player death
        if (!target.IsAlive)
        {
            result.TargetKilled = true;
            OnEntityDeath(target);
        }

        return result;
    }

    private void TriggerActionEffect(
        SimulatedEntityState source,
        SimulatedEntityState target,
        ActionData actionData,
        DamageResult dmgResult)
    {
        try
        {
            // Get source position
            var sourcePos = GetEntityPosition(source);

            bool isRanged = actionData.DamageType == SimDamageType.Magical ||
                            actionData.Range > 5.0f;

            animationController.PlayActionEffect(new ActionEffectRequest
            {
                SourceEntityId = source.EntityId,
                SourcePosition = sourcePos,
                ActionId = actionData.ActionId,
                AnimationLock = actionData.AnimationLock,
                SourceRotation = GetEntityRotation(source),
                IsSourcePlayer = source.IsPlayer,
                IsRanged = isRanged,
                Targets =
                {
                    new TargetEffect
                    {
                        TargetId = target.EntityId,
                        Damage = dmgResult.Damage,
                        IsCritical = dmgResult.IsCritical,
                        IsDirectHit = dmgResult.IsDirectHit,
                        DamageType = dmgResult.DamageType,
                    }
                }
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to trigger action effect animation.");
        }
    }

    private unsafe Vector3 GetEntityPosition(SimulatedEntityState entity)
    {
        if (entity.IsPlayer)
        {
            var player = clientState.LocalPlayer;
            if (player != null)
                return player.Position;
        }
        else
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == entity.EntityId && npc.BattleChara != null)
                {
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                    return go->Position;
                }
            }
        }

        return Vector3.Zero;
    }

    private unsafe float GetEntityRotation(SimulatedEntityState entity)
    {
        if (entity.IsPlayer)
        {
            var player = clientState.LocalPlayer;
            if (player != null)
                return player.Rotation;
        }
        else
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == entity.EntityId && npc.BattleChara != null)
                {
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                    return go->Rotation;
                }
            }
        }

        return 0;
    }

    private void TickPendingDeaths(float deltaTime)
    {
        for (int i = pendingDeaths.Count - 1; i >= 0; i--)
        {
            var pd = pendingDeaths[i];
            pd.Timer -= deltaTime;
            pendingDeaths[i] = pd;

            if (pd.Timer <= 0)
            {
                pendingDeaths.RemoveAt(i);
                ExecuteDeathAnimation(pd.EntityId, pd.IsPlayer);
            }
        }
    }

    private void ExecuteDeathAnimation(ulong entityId, bool isPlayer)
    {
        if (isPlayer)
        {
            if (!playerDeathTriggered)
            {
                playerDeathTriggered = true;
                movementBlockHook.IsBlocking = true;
                animationController.PlayPlayerDeath();
                animationController.PlayVictory(isPlayerVictory: false);
                ApplyGlamourer();
            }
        }
        else
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == entityId)
                {
                    animationController.PlayDeathAnimation(npc);
                    break;
                }
            }

            // Check if all NPCs are dead for victory
            bool allDead = true;
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.State.IsAlive)
                {
                    allDead = false;
                    break;
                }
            }

            if (allDead && !victoryTriggered)
            {
                victoryTriggered = true;
                animationController.PlayVictory(isPlayerVictory: true);
            }
        }
    }

    private void OnEntityDeath(SimulatedEntityState entity)
    {
        AddLogEntry($"{entity.Name} is defeated!", CombatLogType.Death);

        // Queue death animation with a short delay so the killing blow plays first
        pendingDeaths.Add(new PendingDeath
        {
            EntityId = entity.IsPlayer ? 0UL : entity.EntityId,
            IsPlayer = entity.IsPlayer,
            Timer = DeathAnimationDelay,
        });

    }

    private void ApplyGlamourer()
    {
        if (!config.ApplyGlamourerOnDeath || glamourerApplied)
            return;

        if (!Guid.TryParse(config.DeathGlamourerDesignId, out var designId))
            return;

        if (glamourerIpc.ApplyDesign(designId))
        {
            glamourerApplied = true;
            AddLogEntry("Glamourer death preset applied.", CombatLogType.Info);
        }
    }

    private void ApplyResetGlamourer()
    {
        if (!config.ApplyGlamourerOnReset)
            return;

        if (!Guid.TryParse(config.ResetGlamourerDesignId, out var designId))
            return;

        if (glamourerIpc.ApplyDesign(designId))
            AddLogEntry("Glamourer reset preset applied.", CombatLogType.Info);
    }

    private void RevertGlamourer()
    {
        if (!glamourerApplied)
            return;

        glamourerIpc.RevertState();
        glamourerApplied = false;
    }

    private unsafe void InitializePlayerState()
    {
        var player = clientState.LocalPlayer;
        if (player == null)
            return;

        var ps = State.PlayerState;
        ps.EntityId = player.EntityId;
        ps.Name = player.Name.TextValue;
        ps.IsPlayer = true;
        ps.Level = player.Level;
        ps.MaxHp = (int)player.MaxHp;
        ps.CurrentHp = (int)player.MaxHp;
        ps.MaxMp = (int)player.MaxMp;
        ps.CurrentMp = (int)player.MaxMp;

        // Read stats from the actual character
        try
        {
            var battleChara = (BattleChara*)player.Address;
            // These stat positions may vary; using safe defaults if reading fails
            ps.MainStat = 3000; // Approximate for lv90 in decent gear
            ps.Determination = 2000;
            ps.CriticalHit = 2500;
            ps.DirectHit = 1800;
            ps.Defense = 3500;
            ps.MagicDefense = 3500;
        }
        catch
        {
            // Fallback defaults
            ps.MainStat = 2000;
            ps.Determination = 1500;
            ps.CriticalHit = 2000;
            ps.DirectHit = 1500;
            ps.Defense = 3000;
            ps.MagicDefense = 3000;
        }

        ps.Reset();
        ps.MaxHp = (int)player.MaxHp;
        ps.CurrentHp = ps.MaxHp;
        ps.MaxMp = (int)player.MaxMp;
        ps.CurrentMp = ps.MaxMp;
    }

    private void ProcessActionQueue()
    {
        lock (queueLock)
        {
            while (actionQueue.Count > 0)
            {
                var action = actionQueue.Dequeue();
                ProcessPlayerAction(action.ActionId, action.TargetId);
            }
        }
    }

    private void TickCooldowns(SimulatedEntityState entity, float deltaTime)
    {
        var toRemove = new List<int>();
        foreach (var kvp in entity.Cooldowns)
        {
            kvp.Value.Elapsed += deltaTime;
            if (!kvp.Value.IsActive)
                toRemove.Add(kvp.Key);
        }
        foreach (var key in toRemove)
            entity.Cooldowns.Remove(key);

        if (entity.GcdRemaining > 0)
            entity.GcdRemaining = Math.Max(0, entity.GcdRemaining - deltaTime);
    }

    private void TickAnimationLock(SimulatedEntityState entity, float deltaTime)
    {
        if (entity.AnimationLock > 0)
            entity.AnimationLock = Math.Max(0, entity.AnimationLock - deltaTime);
    }

    private void TickCasting(SimulatedEntityState entity, float deltaTime)
    {
        if (!entity.IsCasting)
            return;

        entity.CastTimeElapsed += deltaTime;
        if (entity.CastTimeElapsed >= entity.CastTimeTotal)
        {
            entity.IsCasting = false;
            // Cast completed - action resolves (handled by caller for NPC casts)
        }
    }

    private void TickComboTimer(SimulatedEntityState entity, float deltaTime)
    {
        if (entity.ComboTimer > 0)
        {
            entity.ComboTimer -= deltaTime;
            if (entity.ComboTimer <= 0)
            {
                entity.ComboTimer = 0;
                entity.LastComboAction = 0;
            }
        }
    }

    private void TickStatusEffects(SimulatedEntityState entity, float deltaTime)
    {
        for (int i = entity.StatusEffects.Count - 1; i >= 0; i--)
        {
            var status = entity.StatusEffects[i];
            status.Remaining -= deltaTime;

            if (status.Remaining <= 0)
            {
                entity.StatusEffects.RemoveAt(i);
            }
        }
    }

    private void TickAutoAttack(float deltaTime)
    {
        var ps = State.PlayerState;
        if (!ps.IsAlive)
            return;

        ps.AutoAttackTimer -= deltaTime;
        if (ps.AutoAttackTimer <= 0)
        {
            ps.AutoAttackTimer = 2.56f; // Standard auto-attack delay

            // Auto-attack closest engaged NPC
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.State.IsAlive && npc.IsEngaged)
                {
                    var dmg = damageCalculator.CalculateNpcAutoAttack(ps, npc.State, 110);
                    npc.State.CurrentHp = Math.Max(0, npc.State.CurrentHp - dmg.Damage);
                    State.TotalDamageDealt += dmg.Damage;

                    // Trigger auto-attack animation + VFX
                    var autoAttackData = new ActionData
                    {
                        ActionId = 7, // Auto-attack
                        Potency = 110,
                        DamageType = SimDamageType.Physical,
                        AnimationLock = 0.6f,
                    };
                    TriggerActionEffect(ps, npc.State, autoAttackData, dmg);

                    if (!npc.State.IsAlive)
                        OnEntityDeath(npc.State);

                    break;
                }
            }
        }
    }

    private void TickMpRegen(SimulatedEntityState entity, float deltaTime)
    {
        // MP regenerates at ~200 MP per tick (3s), so ~67/s
        if (entity.CurrentMp < entity.MaxMp)
        {
            entity.CurrentMp = Math.Min(entity.MaxMp,
                entity.CurrentMp + (int)(67 * deltaTime));
        }
    }

    public void RegisterNpcEntity(SimulatedNpc npc)
    {
        State.Entities[npc.SimulatedEntityId] = npc.State;
    }

    public void UnregisterNpcEntity(uint entityId)
    {
        State.Entities.Remove(entityId);
    }

    public void AddLogEntry(string message, CombatLogType type)
    {
        CombatLog.Add(new CombatLogEntry
        {
            Timestamp = State.SimulationTime,
            Message = message,
            Type = type,
        });

        // Cap log size
        while (CombatLog.Count > 1000)
            CombatLog.RemoveAt(0);
    }

    public void Dispose()
    {
        StopSimulation();
    }

    private struct QueuedAction
    {
        public uint ActionType;
        public uint ActionId;
        public ulong TargetId;
        public uint ExtraParam;
    }
}
