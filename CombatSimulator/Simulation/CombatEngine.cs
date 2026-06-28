using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Camera;
using CombatSimulator.Companions;
using CombatSimulator.Integration;
using CombatSimulator.Npcs;
using CombatSimulator.Safety;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GamePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

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

internal readonly record struct AppliedActionDamage(
    SimulatedEntityState Target,
    DamageResult DamageResult);

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
    private readonly RagdollController ragdollController;
    private readonly DeathCamController? deathCamController;
    /// <summary>Optional cinematic victory sequence (dev-only); set after construction. Null = none.</summary>
    public IVictorySequence? VictorySequence { private get; set; }
    private bool playerDeathTriggered;
    private bool victoryTriggered;
    private bool instantRagdollOverride;
    // Player auto-attack is gated on this — when NPCs auto-engage (Dev option),
    // the player does NOT swing back until they manually use an action.
    // Set by ProcessPlayerAction when the player lands a real action; cleared
    // on Start / Reset / Stop.
    private bool playerInitiatedCombat;
    private bool glamourerApplied;

    public SimulationState State { get; } = new();
    public bool IsActive => State.IsActive;
    public List<CombatLogEntry> CombatLog { get; } = new();
    public Action? OnSimulationStarted { get; set; }
    public Func<SimulatedNpc, SimulatedEntityState?>? ResolveNpcTarget { get; set; }
    public Func<uint, nint?>? ResolveExternalEntityAddress { get; set; }
    public Func<bool>? HasLivingCompanions { get; set; }
    /// <summary>
    /// Returns the simulated entity id of the player's currently locked target, or
    /// 0 when none. Wired to PlayerTargetController. When custom targeting is on,
    /// auto-attack only fires against this target.
    /// </summary>
    public Func<uint>? GetLockedTargetId { get; set; }
    /// <summary>
    /// Action Mode: returns true while the target enemy is committed to a telegraphed
    /// attack (winding up), so the hit-reaction flinch is suppressed (super-armor) and
    /// rapid player hits don't visually stunlock it. Wired to TelegraphSystem.
    /// </summary>
    public Func<uint, bool>? IsTargetSuperArmored { get; set; }
    public Action<int>? OnPlayerDamageDealt { get; set; }
    public Action<uint, int>? OnPlayerDamageDealtToTarget { get; set; }
    // Fired when an NPC's attack lands on the (still-alive) player; argument is the
    // attacker's simulated entity id. Drives auto-counter target acquisition.
    public Action<uint>? OnPlayerHitByNpc { get; set; }
    public string LastPlayerDefeatedBy { get; private set; } = string.Empty;
    /// <summary>Address of the NPC that landed the killing blow on the player (0 if none).</summary>
    public nint LastPlayerKillerAddress { get; private set; }

    /// <summary>
    /// Fired at the end of StopSimulation and ResetState — i.e. whenever the
    /// combat session is ended or cleared. Used by the plugin to despawn
    /// virtual enemies and re-queue them fresh, because humanoid clone state
    /// gets broken by the cinema cleanup path and the only reliable fix is
    /// a fresh CopyFromCharacter on the next spawn.
    /// </summary>
    public Action? OnSimulationReset { get; set; }

    /// <summary>
    /// Fired when a simulated NPC dies, passing the NPC's character address.
    /// Subscribers handle ragdoll physics and weapon drop (both with the ragdoll-activation delay).
    /// </summary>
    public Action<nint>? OnNpcDeathRagdoll { get; set; }

    /// <summary>
    /// Fired when the local player dies in the simulation, passing the player's address.
    /// Subscribers handle ragdoll physics and weapon drop (both with the ragdoll-activation delay).
    /// </summary>
    public Action<nint>? OnPlayerDeath { get; set; }

    /// <summary>
    /// Fired immediately before the local player death animation starts.
    /// Experimental modules use this for source objects that must be sampled before
    /// the death pipeline changes the player's visual state.
    /// </summary>
    public Action? BeforePlayerDeath { get; set; }

    /// <summary>
    /// When set and returns true, the Death Cam is not activated on player death — the Fighting
    /// Camera handles the death transition itself so the two don't fight over the camera.
    /// </summary>
    public Func<bool>? SuppressDeathCam { get; set; }

    // Configuration (set from plugin config)
    public float DamageMultiplier { get; set; } = 1.0f;
    public bool EnableCriticalHits { get; set; } = true;
    public bool EnableDirectHits { get; set; } = true;
    private const int GuardMpRestore = 1000;
    private const int BasicAttackMpRestore = 300;

    // Queued player actions (from UseAction hook, processed on framework thread)
    private readonly Queue<QueuedAction> actionQueue = new();
    private readonly object queueLock = new();

    // Pending death animations (delayed so the killing blow visuals play first)
    private readonly List<PendingDeath> pendingDeaths = new();
    private const float DeathAnimationDelay = 0.5f;
    // Action Mode: the player drops almost immediately after the killing blow (the 0.5s gap between
    // the hit-react and the fall felt laggy). Keeps a hair so the hit impact still registers.
    private const float ActionPlayerDeathDelay = 0.1f;

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
        RagdollController ragdollController,
        Configuration config,
        NpcSelector npcSelector,
        IClientState clientState,
        IPluginLog log,
        DeathCamController? deathCamController = null)
    {
        this.actionDataProvider = actionDataProvider;
        this.damageCalculator = damageCalculator;
        this.animationController = animationController;
        this.glamourerIpc = glamourerIpc;
        this.movementBlockHook = movementBlockHook;
        this.ragdollController = ragdollController;
        this.config = config;
        this.npcSelector = npcSelector;
        this.clientState = clientState;
        this.log = log;
        this.deathCamController = deathCamController;
    }

    public void StartSimulation()
    {
        if (State.IsActive)
            return;

        animationController.CapturePlayerCombatVisualState();
        InitializePlayerState();
        State.IsActive = true;
        State.SimulationTime = 0;
        State.CombatStartTime = 0;
        playerDeathTriggered = false;
        LastPlayerDefeatedBy = string.Empty;
        LastPlayerKillerAddress = nint.Zero;
        victoryTriggered = false;
        glamourerApplied = false;
        playerInitiatedCombat = false;
        pendingDeaths.Clear();

        // Apply glamourer combat-ready preset on start
        ApplyResetGlamourer();

        AddLogEntry("Combat simulation started.", CombatLogType.Info);
        log.Info("Combat simulation started.");

        OnSimulationStarted?.Invoke();
    }

    public void StopSimulation()
    {
        if (!State.IsActive)
            return;

        State.IsActive = false;

        lock (queueLock)
            actionQueue.Clear();

        // Stop victory sequence if running
        VictorySequence?.Stop();

        // Clear approach position blocks FIRST so position restores in DeselectAll aren't blocked
        movementBlockHook.ClearApproachNpcs();

        // Clean up all active targets (reset animations, restore ObjectKind, position, etc.)
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
        animationController.RestorePlayerCombatVisualState();
        animationController.RemoveAllActiveVfx();
        movementBlockHook.IsBlocking = false;
        ragdollController.Deactivate();
        deathCamController?.Deactivate();
        RevertGlamourer();
        playerInitiatedCombat = false;
        LastPlayerDefeatedBy = string.Empty;
        LastPlayerKillerAddress = nint.Zero;

        AddLogEntry("Combat simulation stopped.", CombatLogType.Info);
        log.Info("Combat simulation stopped.");

        OnSimulationReset?.Invoke();
    }

    public void ResetState()
    {
        State.Reset();
        CombatLog.Clear();
        VictorySequence?.Stop();
        deathCamController?.Deactivate();

        // Re-initialize player state
        InitializePlayerState();

        // Reset all NPC states
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            npc.State.Reset();
            npc.AiState = Ai.NpcAiState.Idle;
            npc.AutoAttackTimer = 0;
            npc.EngageDelayTimer = 0;
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
        animationController.RestorePlayerCombatVisualState();
        movementBlockHook.IsBlocking = false;
        ragdollController.Deactivate();
        RevertGlamourer();
        ApplyResetGlamourer();

        playerDeathTriggered = false;
        LastPlayerDefeatedBy = string.Empty;
        LastPlayerKillerAddress = nint.Zero;
        victoryTriggered = false;
        glamourerApplied = false;
        playerInitiatedCombat = false;
        pendingDeaths.Clear();

        AddLogEntry("Combat state reset.", CombatLogType.Info);

        OnSimulationReset?.Invoke();
    }

    public void RevivePlayerInPlace()
    {
        var ps = State.PlayerState;
        if (ps.MaxHp <= 0)
            InitializePlayerState();

        ps.CurrentHp = ps.MaxHp;
        ps.CurrentMp = ps.MaxMp;
        ps.AnimationLock = 0;
        ps.GcdRemaining = 0;
        ps.AutoAttackTimer = 0;
        ps.IsCasting = false;
        ps.CastTimeElapsed = 0;
        ps.CastTimeTotal = 0;
        ps.CastActionId = 0;
        ps.CastTargetId = 0;
        ps.Cooldowns.Clear();
        ps.StatusEffects.Clear();

        playerDeathTriggered = false;
        LastPlayerDefeatedBy = string.Empty;
        LastPlayerKillerAddress = nint.Zero;
        animationController.ResetPlayerDeathAnimation();
        animationController.RestorePlayerCombatVisualState();
        movementBlockHook.IsBlocking = false;
        ragdollController.Deactivate();
        deathCamController?.Deactivate();
        RevertGlamourer();
        ApplyResetGlamourer();

        AddLogEntry("Player revived in place.", CombatLogType.Info);
    }

    public void Tick(float deltaTime)
    {
        if (!State.IsActive)
            return;

        State.SimulationTime += deltaTime;

        // Track player's current target for victory sequence
        VictorySequence?.TrackTarget(npcSelector.SelectedNpcs);

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
        var actionData = GetActionDataOrFallback(actionId);

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

        var targets = ResolveActionTargets(State.PlayerState, target, actionData);
        if (targets.Count == 0)
        {
            result.FailReason = "No valid targets.";
            return result;
        }

        if (!TrySpendMp(State.PlayerState, actionData, out var mpFailReason))
        {
            result.FailReason = mpFailReason;
            return result;
        }

        var hits = ApplyDamageToTargets(
            State.PlayerState,
            targets,
            t => damageCalculator.Calculate(
                State.PlayerState, t, ScaleActionPotency(actionData, targets.Count), isCombo,
                EnableCriticalHits, EnableDirectHits, DamageMultiplier));
        if (hits.Count == 0)
        {
            result.FailReason = "No valid targets.";
            return result;
        }

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

        var totalDamage = 0;
        foreach (var hit in hits)
        {
            totalDamage += hit.DamageResult.Damage;
            State.TotalDamageDealt += hit.DamageResult.Damage;
            OnPlayerDamageDealtToTarget?.Invoke(hit.Target.EntityId, hit.DamageResult.Damage);
        }
        OnPlayerDamageDealt?.Invoke(totalDamage);

        // Build result
        result.Success = true;
        result.Damage = totalDamage;
        result.IsCritical = hits[0].DamageResult.IsCritical;
        result.IsDirectHit = hits[0].DamageResult.IsDirectHit;
        result.IsCombo = isCombo;

        // Player chose to fight back — let TickAutoAttack swing from now on.
        playerInitiatedCombat = true;

        // Trigger animation
        TriggerActionEffect(State.PlayerState, actionData, hits);

        // Log
        var primaryHit = hits[0].DamageResult;
        var dmgResult = new DamageResult
        {
            Damage = totalDamage,
            IsCritical = primaryHit.IsCritical,
            IsDirectHit = primaryHit.IsDirectHit,
            DamageType = primaryHit.DamageType,
        };
        var critText = primaryHit.IsCritical ? " critical" : "";
        var dhText = primaryHit.IsDirectHit ? " direct" : "";
        var comboText = isCombo ? " (combo)" : "";
        var aoeText = hits.Count > 1 ? $" +{hits.Count - 1} targets" : "";
        comboText += aoeText;
        AddLogEntry(
            $"You use {actionData.Name} → {target.Name}: {dmgResult.Damage:N0}{critText}{dhText} damage{comboText}",
            CombatLogType.DamageDealt);

        // Engage the NPC if it was idle
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.SimulatedEntityId == (uint)targetId &&
                npc.AiState == Ai.NpcAiState.Idle)
            {
                npc.AiState = Ai.NpcAiState.Engaging;
                npc.EngageDelayTimer = Ai.NpcAiController.PlayerTriggeredEngageDelay;
                Ai.NpcAiController.StaggerTimers(npc);
                AddLogEntry($"{npc.Name} engages!", CombatLogType.Info);
                break;
            }
        }

        // Check death
        foreach (var hit in hits)
        {
            if (!hit.Target.IsAlive)
            {
                if (hit.Target.EntityId == target.EntityId)
                    result.TargetKilled = true;
                OnEntityDeath(hit.Target);
            }
        }

        return result;
    }

    public SimulatedActionResult ProcessNpcAction(
        SimulatedNpc npc,
        uint actionId,
        ulong targetId,
        int potency = 0,
        NpcAttackStyle attackStyle = NpcAttackStyle.Auto,
        float radius = 0,
        bool suppressCasterActionEffect = false)
    {
        var result = new SimulatedActionResult
        {
            ActionId = actionId,
            SourceId = npc.SimulatedEntityId,
            TargetId = targetId,
        };

        var target = State.GetEntity(targetId);
        if (target == null || !npc.State.IsAlive || !target.IsAlive)
        {
            result.FailReason = "Invalid target or source dead.";
            return result;
        }

        var resolvedActionData = actionDataProvider.GetActionData(actionId);

        // Calculate damage
        DamageResult dmgResult;
        if (potency > 0)
        {
            dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target, potency);
        }
        else
        {
            if (resolvedActionData != null)
            {
                dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target, resolvedActionData.Potency);
            }
            else
            {
                dmgResult = damageCalculator.CalculateNpcAutoAttack(npc.State, target);
            }
        }

        // Apply damage to target
        target.CurrentHp = Math.Max(0, target.CurrentHp - dmgResult.Damage);
        if (target.IsPlayer)
        {
            State.TotalDamageTaken += dmgResult.Damage;
            // Player was hit and survived → let auto-counter consider locking this attacker.
            if (target.IsAlive)
                OnPlayerHitByNpc?.Invoke(npc.SimulatedEntityId);
        }

        // Trigger animation (NPC -> Player). Preserve the selected action id
        // so ranged/caster skills can use their own timelines/VFX instead of
        // every enemy action collapsing to ActionId 7.
        var visualAction = resolvedActionData != null ? CloneActionData(resolvedActionData) : new ActionData
        {
            ActionId = actionId == 0 ? 7 : actionId,
            Name = actionId == 7 ? "Auto-attack" : $"Action #{actionId}",
            Potency = potency > 0 ? potency : 110,
            DamageType = SimDamageType.Physical,
            Range = npc.Behavior.AutoAttackRange,
            AnimationLock = 0.6f,
        };
        if (potency > 0)
            visualAction.Potency = potency;
        if (radius > 0)
        {
            visualAction.Radius = radius;
            if (visualAction.Shape == AoeShape.Single)
                visualAction.Shape = AoeShape.CircleSelf;
        }

        ApplyNpcAttackStyle(visualAction, attackStyle == NpcAttackStyle.Auto ? npc.Behavior.AutoAttackStyle : attackStyle);
        var hits = new List<AppliedActionDamage> { new(target, dmgResult) };
        var totalDamage = dmgResult.Damage;
        foreach (var extraTarget in ResolveActionTargets(npc.State, target, visualAction))
        {
            if (extraTarget.EntityId == target.EntityId)
                continue;

            var extraDamage = damageCalculator.CalculateNpcAutoAttack(
                npc.State,
                extraTarget,
                visualAction.Potency > 0 ? visualAction.Potency : 110);
            extraTarget.CurrentHp = Math.Max(0, extraTarget.CurrentHp - extraDamage.Damage);
            hits.Add(new AppliedActionDamage(extraTarget, extraDamage));
            totalDamage += extraDamage.Damage;

            if (extraTarget.IsPlayer)
            {
                State.TotalDamageTaken += extraDamage.Damage;
                if (extraTarget.IsAlive)
                    OnPlayerHitByNpc?.Invoke(npc.SimulatedEntityId);
            }
        }

        if (suppressCasterActionEffect)
            TriggerManualNpcHitFeedback(hits);
        else
            TriggerActionEffect(npc.State, visualAction, hits);

        result.Success = true;
        result.Damage = totalDamage;
        result.IsCritical = dmgResult.IsCritical;
        result.IsDirectHit = dmgResult.IsDirectHit;

        // Log
        var actionName = actionId == 7
            ? "auto-attacks"
            : $"uses {(!string.IsNullOrWhiteSpace(visualAction.Name) ? visualAction.Name : "action")}";
        // TODO: companion targets still render as "You" in this legacy log line.
        AddLogEntry(
            $"{npc.Name} {actionName} → You: {dmgResult.Damage:N0} damage",
            CombatLogType.DamageTaken);

        // Check player death
        if (!target.IsAlive)
        {
            if (target.IsPlayer)
            {
                LastPlayerDefeatedBy = npc.Name;
                LastPlayerKillerAddress = npc.Address;
            }
            result.TargetKilled = true;
            OnEntityDeath(target);
        }
        foreach (var hit in hits)
        {
            if (hit.Target.EntityId == target.EntityId || hit.Target.IsAlive)
                continue;

            if (hit.Target.IsPlayer)
            {
                LastPlayerDefeatedBy = npc.Name;
                LastPlayerKillerAddress = npc.Address;
            }
            OnEntityDeath(hit.Target);
        }

        return result;
    }

    public SimulatedActionResult ProcessCompanionAction(
        CombatCompanion companion,
        SimulatedNpc targetNpc,
        uint actionId,
        int potency = 0,
        NpcAttackStyle attackStyle = NpcAttackStyle.Auto,
        float radius = 0)
    {
        var result = new SimulatedActionResult
        {
            ActionId = actionId,
            SourceId = companion.SimulatedEntityId,
            TargetId = targetNpc.SimulatedEntityId,
        };

        if (!companion.State.IsAlive || !targetNpc.State.IsAlive)
        {
            result.FailReason = "Invalid target or source dead.";
            return result;
        }

        var resolvedActionData = actionDataProvider.GetActionData(actionId);
        DamageResult dmgResult;
        if (potency > 0)
            dmgResult = damageCalculator.CalculateNpcAutoAttack(companion.State, targetNpc.State, potency);
        else if (resolvedActionData != null)
            dmgResult = damageCalculator.Calculate(companion.State, targetNpc.State, resolvedActionData, false, EnableCriticalHits, EnableDirectHits, DamageMultiplier);
        else
            dmgResult = damageCalculator.CalculateNpcAutoAttack(companion.State, targetNpc.State);

        targetNpc.State.CurrentHp = Math.Max(0, targetNpc.State.CurrentHp - dmgResult.Damage);
        State.TotalDamageDealt += dmgResult.Damage;

        // Companion aggro: a companion attacking an idle enemy makes it fight back,
        // mirroring how the player's attack engages a target. Without this, enemies
        // stay idle until the player personally attacks them.
        if (targetNpc.AiState == Ai.NpcAiState.Idle)
        {
            targetNpc.AiState = Ai.NpcAiState.Engaging;
            targetNpc.EngageDelayTimer = Ai.NpcAiController.PlayerTriggeredEngageDelay;
            Ai.NpcAiController.StaggerTimers(targetNpc);
            AddLogEntry($"{targetNpc.Name} engages!", CombatLogType.Info);
        }

        var visualAction = resolvedActionData != null ? CloneActionData(resolvedActionData) : new ActionData
        {
            ActionId = actionId == 0 ? 7 : actionId,
            Name = actionId == 7 ? "Auto-attack" : $"Action #{actionId}",
            Potency = potency > 0 ? potency : 110,
            DamageType = SimDamageType.Physical,
            Range = companion.Behavior.AutoAttackRange,
            AnimationLock = 0.6f,
        };
        if (potency > 0)
            visualAction.Potency = potency;
        if (radius > 0)
        {
            visualAction.Radius = radius;
            if (visualAction.Shape == AoeShape.Single)
                visualAction.Shape = AoeShape.CircleSelf;
        }

        ApplyNpcAttackStyle(visualAction, attackStyle == NpcAttackStyle.Auto ? companion.Behavior.AutoAttackStyle : attackStyle);
        var hits = new List<AppliedActionDamage> { new(targetNpc.State, dmgResult) };
        var totalDamage = dmgResult.Damage;
        foreach (var extraTarget in ResolveActionTargets(companion.State, targetNpc.State, visualAction))
        {
            if (extraTarget.EntityId == targetNpc.SimulatedEntityId)
                continue;

            var extraDamage = potency > 0
                ? damageCalculator.CalculateNpcAutoAttack(companion.State, extraTarget, visualAction.Potency)
                : resolvedActionData != null
                    ? damageCalculator.Calculate(companion.State, extraTarget, visualAction, false, EnableCriticalHits, EnableDirectHits, DamageMultiplier)
                    : damageCalculator.CalculateNpcAutoAttack(companion.State, extraTarget);
            extraTarget.CurrentHp = Math.Max(0, extraTarget.CurrentHp - extraDamage.Damage);
            hits.Add(new AppliedActionDamage(extraTarget, extraDamage));
            totalDamage += extraDamage.Damage;
            State.TotalDamageDealt += extraDamage.Damage;

            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId != extraTarget.EntityId || npc.AiState != Ai.NpcAiState.Idle)
                    continue;

                npc.AiState = Ai.NpcAiState.Engaging;
                npc.EngageDelayTimer = Ai.NpcAiController.PlayerTriggeredEngageDelay;
                Ai.NpcAiController.StaggerTimers(npc);
                AddLogEntry($"{npc.Name} engages!", CombatLogType.Info);
                break;
            }
        }

        TriggerActionEffect(companion.State, visualAction, hits);

        result.Success = true;
        result.Damage = totalDamage;
        result.IsCritical = dmgResult.IsCritical;
        result.IsDirectHit = dmgResult.IsDirectHit;

        var actionName = actionId == 7
            ? "auto-attacks"
            : $"uses {(!string.IsNullOrWhiteSpace(visualAction.Name) ? visualAction.Name : "action")}";
        AddLogEntry(
            $"{companion.Name} {actionName} -> {targetNpc.Name}: {dmgResult.Damage:N0} damage",
            CombatLogType.DamageDealt);

        if (!targetNpc.State.IsAlive)
        {
            result.TargetKilled = true;
            OnEntityDeath(targetNpc.State);
        }
        foreach (var hit in hits)
        {
            if (hit.Target.EntityId == targetNpc.SimulatedEntityId || hit.Target.IsAlive)
                continue;

            OnEntityDeath(hit.Target);
        }

        return result;
    }

    public SimulatedEntityState GetNpcTarget(SimulatedNpc npc)
        => ResolveNpcTarget?.Invoke(npc) ?? State.PlayerState;

    public Vector3 GetSimulatedEntityPosition(SimulatedEntityState entity)
        => GetEntityPosition(entity);

    private List<AppliedActionDamage> ApplyDamageToTargets(
        SimulatedEntityState source,
        IReadOnlyList<SimulatedEntityState> targets,
        Func<SimulatedEntityState, DamageResult> calculateDamage)
    {
        var hits = new List<AppliedActionDamage>();
        foreach (var target in targets)
        {
            if (!target.IsAlive || hits.Count >= 32)
                continue;

            var damage = calculateDamage(target);
            target.CurrentHp = Math.Max(0, target.CurrentHp - damage.Damage);
            hits.Add(new AppliedActionDamage(target, damage));
        }

        return hits;
    }

    private List<SimulatedEntityState> ResolveActionTargets(
        SimulatedEntityState source,
        SimulatedEntityState primaryTarget,
        ActionData actionData)
    {
        var targets = new List<SimulatedEntityState>();
        if (!primaryTarget.IsAlive || !IsHostile(source, primaryTarget))
            return targets;

        targets.Add(primaryTarget);
        if (actionData.Shape == AoeShape.Single || actionData.Radius <= 0)
            return targets;

        foreach (var candidate in GetHostileTargets(source))
        {
            if (targets.Count >= 32)
                break;
            if (candidate.EntityId == primaryTarget.EntityId || !candidate.IsAlive)
                continue;
            if (IsInsideActionArea(source, primaryTarget, candidate, actionData))
                targets.Add(candidate);
        }

        return targets;
    }

    private List<SimulatedEntityState> GetHostileTargets(SimulatedEntityState source)
    {
        var result = new List<SimulatedEntityState>();
        if (IsFriendly(source))
        {
            foreach (var entity in State.Entities.Values)
            {
                if (!entity.IsPlayer && !entity.IsCompanion && entity.IsAlive)
                    result.Add(entity);
            }
            return result;
        }

        if (State.PlayerState.IsAlive)
            result.Add(State.PlayerState);
        foreach (var entity in State.Entities.Values)
        {
            if (entity.IsCompanion && entity.IsAlive)
                result.Add(entity);
        }
        return result;
    }

    private bool IsInsideActionArea(
        SimulatedEntityState source,
        SimulatedEntityState primaryTarget,
        SimulatedEntityState candidate,
        ActionData actionData)
    {
        var sourcePos = GetEntityPosition(source);
        var primaryPos = GetEntityPosition(primaryTarget);
        var candidatePos = GetEntityPosition(candidate);
        var radius = MathF.Max(0f, actionData.Radius);

        return actionData.Shape switch
        {
            AoeShape.Circle or AoeShape.GroundCircle =>
                CombatGeometry.IsInsideCircle(primaryPos, candidatePos, radius),
            AoeShape.CircleSelf or AoeShape.Donut =>
                CombatGeometry.IsInsideCircle(sourcePos, candidatePos, radius),
            AoeShape.Cone =>
                CombatGeometry.IsInsideCone(sourcePos, primaryPos, candidatePos, radius),
            AoeShape.Line =>
                CombatGeometry.IsInsideLine(sourcePos, primaryPos, candidatePos, radius, actionData.Width),
            _ => false,
        };
    }

    private static bool IsFriendly(SimulatedEntityState entity)
        => entity.IsPlayer || entity.IsCompanion;

    private static bool IsHostile(SimulatedEntityState a, SimulatedEntityState b)
        => IsFriendly(a) != IsFriendly(b);

    // 2D hit-shape tests moved to CombatGeometry so the Action-Mode telegraph +
    // player hitbox share the exact same geometry as AoE resolution.

    private static void ApplyNpcAttackStyle(ActionData actionData, NpcAttackStyle attackStyle)
    {
        switch (attackStyle)
        {
            case NpcAttackStyle.Ranged:
                actionData.Range = Math.Max(actionData.Range, 20f);
                break;
            case NpcAttackStyle.Magic:
                actionData.Range = Math.Max(actionData.Range, 20f);
                actionData.DamageType = SimDamageType.Magical;
                break;
            case NpcAttackStyle.Melee:
                if (actionData.Range <= 0 || actionData.Range > 5f)
                    actionData.Range = 3f;
                break;
        }
    }

    private static ActionData CloneActionData(ActionData source)
    {
        return new ActionData
        {
            ActionId = source.ActionId,
            Name = source.Name,
            Potency = source.Potency,
            CastTime = source.CastTime,
            RecastTime = source.RecastTime,
            RecastGroup = source.RecastGroup,
            Range = source.Range,
            Radius = source.Radius,
            Shape = source.Shape,
            Width = source.Width,
            DamageType = source.DamageType,
            NativeMpCost = source.NativeMpCost,
            MpCost = source.MpCost,
            IsComboAction = source.IsComboAction,
            ComboFrom = source.ComboFrom,
            ComboPotency = source.ComboPotency,
            AnimationLock = source.AnimationLock,
            AnimationDuration = source.AnimationDuration,
            IsPlayerAction = source.IsPlayerAction,
            AnimationStartTimelineId = source.AnimationStartTimelineId,
            AnimationEndTimelineId = source.AnimationEndTimelineId,
            CastVfxPath = source.CastVfxPath,
            StartVfxPath = source.StartVfxPath,
            CasterVfxPaths = new List<string>(source.CasterVfxPaths),
            TargetVfxPaths = new List<string>(source.TargetVfxPaths),
        };
    }

    private void TriggerActionEffect(
        SimulatedEntityState source,
        SimulatedEntityState target,
        ActionData actionData,
        DamageResult dmgResult)
        => TriggerActionEffect(source, actionData, new List<AppliedActionDamage> { new(target, dmgResult) });

    private void TriggerActionEffect(
        SimulatedEntityState source,
        ActionData actionData,
        IReadOnlyList<AppliedActionDamage> hits)
    {
        if (hits.Count == 0)
            return;

        try
        {
            // Get source position
            var sourcePos = GetEntityPosition(source);

            bool isRanged = actionData.DamageType == SimDamageType.Magical ||
                            actionData.Range > 5.0f;

            // Per-NPC ranged override: an NPC marked IsRanged in the spawned list
            // always plays the ranged attack motion regardless of action data.
            if (!source.IsPlayer)
            {
                foreach (var sourceNpc in npcSelector.SelectedNpcs)
                {
                    if (sourceNpc.SimulatedEntityId != source.EntityId)
                        continue;

                    NpcAttackStyle weaponStyle;
                    unsafe
                    {
                        weaponStyle = sourceNpc.BattleChara != null
                            ? NpcWeaponClassifier.DetectFromCharacter((Character*)sourceNpc.BattleChara, log, sourceNpc.Name)
                            : NpcAttackStyle.Auto;
                    }

                    if (weaponStyle == NpcAttackStyle.Ranged)
                    {
                        isRanged = true;
                        actionData.Range = Math.Max(actionData.Range, 20f);
                        break;
                    }

                    if (sourceNpc.IsRanged)
                    {
                        isRanged = true;
                        actionData.Range = Math.Max(actionData.Range, 20f);
                        break;
                    }
                }
            }

            // Map simulated entity IDs to real game object IDs for native calls.
            // ActionEffectHandler.Receive needs IDs the game engine can resolve.
            var gameSourceId = GetGameEntityId(source);

            var request = new ActionEffectRequest
            {
                SourceEntityId = gameSourceId,
                SourcePosition = sourcePos,
                ActionId = actionData.ActionId,
                AnimationLock = actionData.AnimationLock,
                SourceRotation = GetEntityRotation(source),
                IsSourcePlayer = source.IsPlayer,
                IsRanged = isRanged,
                AttackStyle = actionData.DamageType == SimDamageType.Magical
                    ? NpcAttackStyle.Magic
                    : isRanged
                        ? NpcAttackStyle.Ranged
                        : NpcAttackStyle.Melee,
                AnimationStartTimelineId = actionData.AnimationStartTimelineId,
                AnimationEndTimelineId = actionData.AnimationEndTimelineId,
                CastVfxPath = actionData.CastVfxPath,
                StartVfxPath = actionData.StartVfxPath,
                CasterVfxPaths = actionData.CasterVfxPaths,
                TargetVfxPaths = actionData.TargetVfxPaths,
            };

            foreach (var hit in hits)
            {
                request.Targets.Add(new TargetEffect
                {
                    TargetId = GetGameEntityId(hit.Target),
                    Damage = hit.DamageResult.Damage,
                    IsCritical = hit.DamageResult.IsCritical,
                    IsDirectHit = hit.DamageResult.IsDirectHit,
                    DamageType = hit.DamageResult.DamageType,
                });
            }

            animationController.PlayActionEffect(request);

            // ActionEffectHandler.Receive's internal target lookup typically fails for
            // client-spawned NPCs (0xF000xxxx EntityIds aren't in CharacterManager),
            // so the natural hit reaction never fires. Play the damage timeline
            // directly on the target so it visibly flinches when hit.
            foreach (var hit in hits)
                PlayHitReactionOnTarget(hit.Target, hit.DamageResult.Damage > 0);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to trigger action effect animation.");
        }
    }

    private void TriggerManualNpcHitFeedback(IReadOnlyList<AppliedActionDamage> hits)
    {
        foreach (var hit in hits)
        {
            if (hit.DamageResult.Damage <= 0)
                continue;

            PlayHitReactionOnTarget(hit.Target, isDamage: true, includePlayer: true);
        }
    }

    // Action-Mode hit reaction: play the additive "battle/damage" flinch on whoever got hit.
    // (Not ActionTimeline 78 — that's the guard/parry pose, wrong in a parry mode.)
    private unsafe void PlayHitReactionOnTarget(SimulatedEntityState target, bool isDamage, bool includePlayer = false)
    {
        if (!isDamage || (target.IsPlayer && !includePlayer)) return;

        // Super-armor: a target mid-telegraph commits to its attack — no reaction.
        if (IsTargetSuperArmored?.Invoke(target.EntityId) == true) return;

        var addr = ResolveTargetAddress(target);
        if (addr != nint.Zero)
            animationController.PlayHitReactionAnimation(addr);
    }

    private unsafe nint ResolveTargetAddress(SimulatedEntityState target)
    {
        if (target.IsPlayer)
            return Core.Services.ObjectTable.LocalPlayer?.Address ?? nint.Zero;

        if (target.IsCompanion)
            return ResolveExternalEntityAddress?.Invoke(target.EntityId) ?? nint.Zero;

        foreach (var npc in npcSelector.SelectedNpcs)
            if (npc.SimulatedEntityId == target.EntityId && npc.BattleChara != null)
                return (nint)npc.BattleChara;

        return nint.Zero;
    }

    /// <summary>
    /// Maps a SimulatedEntityState's EntityId to the real game object EntityId.
    /// For player, returns the player's real EntityId.
    /// For spawned NPCs, reads the EntityId from the BattleChara game object
    /// (which differs from our internally assigned SimulatedEntityId).
    /// Native code like ActionEffectHandler.Receive needs real game IDs, not our fake ones.
    /// </summary>
    private unsafe uint GetGameEntityId(SimulatedEntityState entity)
    {
        if (entity.IsPlayer)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
                return player.EntityId;
        }
        else if (entity.IsCompanion)
        {
            var addr = ResolveExternalEntityAddress?.Invoke(entity.EntityId);
            if (addr.HasValue && addr.Value != nint.Zero)
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)addr.Value;
                return obj->GetGameObjectId().ObjectId;
            }
        }
        else
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == entity.EntityId && npc.BattleChara != null)
                {
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
                    return go->GetGameObjectId().ObjectId;
                }
            }
        }

        return entity.EntityId;
    }

    private unsafe Vector3 GetEntityPosition(SimulatedEntityState entity)
    {
        if (entity.IsPlayer)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
                return player.Position;
        }
        else if (entity.IsCompanion)
        {
            var addr = ResolveExternalEntityAddress?.Invoke(entity.EntityId);
            if (addr.HasValue && addr.Value != nint.Zero)
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)addr.Value;
                return obj->Position;
            }
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
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
                return player.Rotation;
        }
        else if (entity.IsCompanion)
        {
            var addr = ResolveExternalEntityAddress?.Invoke(entity.EntityId);
            if (addr.HasValue && addr.Value != nint.Zero)
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)addr.Value;
                return obj->Rotation;
            }
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
                animationController.RemoveAllActiveVfx();
                BeforePlayerDeath?.Invoke();
                animationController.PlayPlayerDeath(forceCombatDeath: true);
                TriggerEnemyVictoryIfPartyDefeated();
                ApplyGlamourer();
                if (!(SuppressDeathCam?.Invoke() ?? false))
                    deathCamController?.Activate();

                // Activate ragdoll physics on player death + fire weapon-drop hook
                {
                    var player = Core.Services.ObjectTable.LocalPlayer;
                    if (player != null && player.Address != nint.Zero)
                    {
                        // Weapon drop is part of ragdoll, so only fire it (and the
                        // ragdoll) when ragdoll is enabled.
                        if (config.EnableRagdoll)
                        {
                            var ragdollDelay = instantRagdollOverride ? 0f : config.RagdollActivationDelay;
                            ragdollController.Activate(player.Address, ragdollDelay);
                            OnPlayerDeath?.Invoke(player.Address);
                        }
                    }
                }

            }
        }
        else
        {
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == entityId)
                {
                    animationController.PlayDeathAnimation(npc);

                    // Trigger NPC death ragdoll
                    if (npc.Address != nint.Zero)
                        OnNpcDeathRagdoll?.Invoke(npc.Address);

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

    /// <summary>
    /// Instantly kills the player through the normal death pipeline, using zero ragdoll delay.
    /// Requires the simulation to already be active. Used by BoneHoldTestMode.
    /// </summary>
    public void ForcePlayerInstantDeath()
    {
        if (!State.IsActive || playerDeathTriggered) return;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;
        State.PlayerState.CurrentHp = 0;
        instantRagdollOverride = true;
        ExecuteDeathAnimation(player.EntityId, isPlayer: true);
        instantRagdollOverride = false;
    }

    public void TriggerEnemyVictoryIfPartyDefeated()
    {
        if (victoryTriggered)
            return;
        if (State.PlayerState.IsAlive)
            return;
        if (HasLivingCompanions?.Invoke() == true)
            return;

        victoryTriggered = true;

        // Try cinematic victory sequence first; fall back to normal emotes.
        SimulatedNpc? cinematicNpc = null;
        if (VictorySequence != null)
        {
            var (started, cNpc) = VictorySequence.TryStart(npcSelector.SelectedNpcs);
            if (started) cinematicNpc = cNpc;
        }

        if (cinematicNpc == null)
            animationController.PlayVictory(isPlayerVictory: false, npcSelector.SelectedNpcs);
    }

    private void OnEntityDeath(SimulatedEntityState entity)
    {
        AddLogEntry($"{entity.Name} is defeated!", CombatLogType.Death);

        if (entity.IsCompanion)
        {
            TriggerEnemyVictoryIfPartyDefeated();
            return;
        }

        // Queue death animation with a short delay so the killing blow plays first. In Action Mode
        // the player drops fast so the fall isn't lagging hundreds of ms behind the hit-react.
        pendingDeaths.Add(new PendingDeath
        {
            EntityId = entity.IsPlayer ? 0UL : entity.EntityId,
            IsPlayer = entity.IsPlayer,
            Timer = entity.IsPlayer && config.ActionMode ? ActionPlayerDeathDelay : DeathAnimationDelay,
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

    /// <summary>
    /// Ensure the local player's combat stats have been read at least once.
    /// Companions mirror these values, and in Professional Mode they can be
    /// spawned before the simulation starts, so this lets callers populate
    /// PlayerState on demand without starting combat.
    /// </summary>
    public void EnsurePlayerInitialized()
    {
        if (State.PlayerState.MaxHp <= 0)
            InitializePlayerState();
    }

    private unsafe void InitializePlayerState()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
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
            var playerState = GamePlayerState.Instance();
            if (playerState == null)
                throw new InvalidOperationException("PlayerState unavailable.");

            ps.ClassJobId = player.ClassJob.RowId;
            ps.AttackPower = playerState->GetAttributeByIndex(PlayerAttribute.AttackPower);
            ps.AttackMagicPotency = playerState->GetAttributeByIndex(PlayerAttribute.AttackMagicPotency);
            ps.HealingMagicPotency = playerState->GetAttributeByIndex(PlayerAttribute.HealingMagicPotency);
            ps.Determination = playerState->GetAttributeByIndex(PlayerAttribute.Determination);
            ps.CriticalHit = playerState->GetAttributeByIndex(PlayerAttribute.CriticalHit);
            ps.DirectHit = playerState->GetAttributeByIndex(PlayerAttribute.DirectHitRate);
            ps.Defense = playerState->GetAttributeByIndex(PlayerAttribute.Defense);
            ps.MagicDefense = playerState->GetAttributeByIndex(PlayerAttribute.MagicDefense);
            ps.Tenacity = playerState->GetAttributeByIndex(PlayerAttribute.Tenacity);
            ps.SkillSpeed = playerState->GetAttributeByIndex(PlayerAttribute.SkillSpeed);
            ps.SpellSpeed = playerState->GetAttributeByIndex(PlayerAttribute.SpellSpeed);
            ps.WeaponDamage = playerState->GetAttributeByIndex(PlayerAttribute.PhysicalDamage);
            ps.MagicWeaponDamage = playerState->GetAttributeByIndex(PlayerAttribute.MagicDamage);
            ps.WeaponDelayMs = playerState->GetAttributeByIndex(PlayerAttribute.Delay);
            ps.IsTank = IsTankJob(ps.ClassJobId);
            ps.DamageTraitPct = 100;
            ps.MainStat = Math.Max(ps.AttackPower, ps.AttackMagicPotency);
        }
        catch
        {
            // Fallback defaults
            ps.MainStat = 2000;
            ps.AttackPower = ps.MainStat;
            ps.AttackMagicPotency = ps.MainStat;
            ps.Determination = 1500;
            ps.CriticalHit = 2000;
            ps.DirectHit = 1500;
            ps.Defense = 3000;
            ps.MagicDefense = 3000;
            ps.Tenacity = 400;
            ps.SkillSpeed = 400;
            ps.SpellSpeed = 400;
            ps.WeaponDamage = 120;
            ps.MagicWeaponDamage = 120;
            ps.WeaponDelayMs = 3000;
            ps.IsTank = false;
            ps.DamageTraitPct = 100;
        }

        ps.Reset();
        ps.MaxHp = (int)player.MaxHp;
        ps.CurrentHp = ps.MaxHp;
        ps.MaxMp = (int)player.MaxMp;
        ps.CurrentMp = ps.MaxMp;
    }

    private static bool IsTankJob(uint classJobId)
        => classJobId is 19 or 21 or 32 or 37;

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
        // Action Mode replaces the timed auto-attack with button-driven combos.
        if (config.ActionMode)
            return;

        var ps = State.PlayerState;
        if (!ps.IsAlive)
            return;

        if (config.EnableCustomTargeting)
        {
            // Custom targeting: only auto-attack the locked target. No lock => no
            // swings (matches "主角有目标时才发动攻击").
            var lockedId = GetLockedTargetId?.Invoke() ?? 0u;
            if (lockedId == 0u)
                return;

            SimulatedNpc? target = null;
            foreach (var npc in npcSelector.SelectedNpcs)
            {
                if (npc.SimulatedEntityId == lockedId)
                {
                    target = npc;
                    break;
                }
            }
            if (target == null || !target.State.IsAlive)
                return;

            // Locking onto an enemy and swinging commits to the fight — engage it
            // if it was still idle (mirrors ProcessPlayerAction).
            if (target.AiState == Ai.NpcAiState.Idle)
            {
                target.AiState = Ai.NpcAiState.Engaging;
                target.EngageDelayTimer = Ai.NpcAiController.PlayerTriggeredEngageDelay;
                Ai.NpcAiController.StaggerTimers(target);
                AddLogEntry($"{target.Name} engages!", CombatLogType.Info);
            }

            ps.AutoAttackTimer -= deltaTime;
            if (ps.AutoAttackTimer <= 0)
            {
                ps.AutoAttackTimer = 2.56f; // Standard auto-attack delay
                AutoAttackNpc(ps, target);
            }
            return;
        }

        // Legacy behavior (custom targeting disabled): don't auto-swing just because
        // something engaged us — wait until the player chooses to fight.
        if (!playerInitiatedCombat)
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
                    AutoAttackNpc(ps, npc);
                    break;
                }
            }
        }
    }

    private void AutoAttackNpc(SimulatedEntityState ps, SimulatedNpc npc)
    {
        var dmg = damageCalculator.CalculateNpcAutoAttack(ps, npc.State, 110);
        npc.State.CurrentHp = Math.Max(0, npc.State.CurrentHp - dmg.Damage);
        State.TotalDamageDealt += dmg.Damage;
        OnPlayerDamageDealt?.Invoke(dmg.Damage);
        OnPlayerDamageDealtToTarget?.Invoke(npc.State.EntityId, dmg.Damage);

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
    }

    public ActionData? GetActionData(uint actionId) => actionDataProvider.GetActionData(actionId);

    public ActionData GetActionDataOrFallback(uint actionId, float animationDuration = 0f)
    {
        var source = actionDataProvider.GetActionData(actionId);
        if (source != null)
        {
            var data = CloneActionData(source);
            ApplyActionModeAnimationDuration(data, animationDuration);
            return data;
        }

        var fallback = new ActionData
        {
            ActionId = actionId == 0 ? 7u : actionId,
            Name = actionId == 7 ? "Attack" : $"Action #{actionId}",
            RecastTime = 2.5f,
            RecastGroup = 58,
            Range = 3f,
            Shape = AoeShape.Single,
            DamageType = SimDamageType.Physical,
            AnimationLock = 0.6f,
        };
        ApplyActionModeAnimationDuration(fallback, animationDuration);
        VirtualActionModel.Apply(fallback, config.LightAttackPotency);
        return fallback;
    }

    private void ApplyActionModeAnimationDuration(ActionData actionData, float animationDuration)
    {
        if (animationDuration <= 0.05f)
            return;

        actionData.AnimationDuration = animationDuration;
        VirtualActionModel.Apply(actionData, config.LightAttackPotency);
    }

    public bool TrySpendPlayerActionMp(uint actionId, out ActionData actionData, out string? failReason)
        => TrySpendPlayerActionMp(actionId, 0f, out actionData, out failReason);

    public bool TrySpendPlayerActionMp(uint actionId, float animationDuration, out ActionData actionData, out string? failReason)
    {
        actionData = GetActionDataOrFallback(actionId, animationDuration);
        return TrySpendMp(State.PlayerState, actionData, out failReason);
    }

    public void RestorePlayerGuardMp(int guardCount)
    {
        if (guardCount <= 0)
            return;

        RestoreMp(State.PlayerState, GuardMpRestore * guardCount);
    }

    /// <summary>
    /// Action-Mode player attack: given a soft-target primary, fan out the action's REAL shape via
    /// <see cref="ResolveActionTargets"/> and apply with its real potency (or an override for the
    /// basic attack). Reuses the same shape module + feedback (swing/impact sound/hit-react/flytext)
    /// normal mode uses. Returns the number of enemies hit; 0 = whiff (caller plays animation-only).
    /// </summary>
    public int ApplyPlayerActionMode(uint actionId, ulong primaryEntityId, int potencyOverride = 0, float animationDuration = 0f)
    {
        var ps = State.PlayerState;
        if (!ps.IsAlive)
            return 0;

        var primary = State.GetEntity(primaryEntityId);
        if (primary == null || !primary.IsAlive || !IsHostile(ps, primary))
            return 0;

        var actionData = GetActionDataOrFallback(actionId, animationDuration);
        if (potencyOverride > 0)
        {
            actionData.Potency = potencyOverride;
            actionData.MpCost = 0;
        }

        // Action Mode has no placed ground/target reticle, so a circle AoE centres on the PLAYER
        // (full ring around you) instead of on the front-picked primary — fixes "circle only hits in
        // front". Cones/lines stay directional.
        if (actionData.Shape is AoeShape.Circle or AoeShape.GroundCircle)
            actionData.Shape = AoeShape.CircleSelf;

        var hits = new List<AppliedActionDamage>();
        var total = 0;
        var targets = ResolveActionTargets(ps, primary, actionData);
        var damageAction = ScaleActionPotency(actionData, targets.Count);
        foreach (var target in targets)
        {
            if (target.IsPlayer || target.IsCompanion || !target.IsAlive)
                continue;
            var dmg = damageCalculator.Calculate(
                ps, target, damageAction, false,
                EnableCriticalHits, EnableDirectHits, DamageMultiplier);
            target.CurrentHp = Math.Max(0, target.CurrentHp - dmg.Damage);
            hits.Add(new AppliedActionDamage(target, dmg));
            total += dmg.Damage;
            State.TotalDamageDealt += dmg.Damage;
            OnPlayerDamageDealtToTarget?.Invoke(target.EntityId, dmg.Damage);
            EngageIdleTarget(target.EntityId);
        }

        if (hits.Count == 0)
            return 0;

        if (State.CombatStartTime == 0)
            State.CombatStartTime = State.SimulationTime;

        if (actionId == 7 && potencyOverride > 0)
            RestoreMp(ps, BasicAttackMpRestore);

        OnPlayerDamageDealt?.Invoke(total);
        TriggerActionEffect(ps, actionData, hits);

        foreach (var hit in hits)
            if (!hit.Target.IsAlive)
                OnEntityDeath(hit.Target);

        return hits.Count;
    }

    private static ActionData ScaleActionPotency(ActionData actionData, int actualTargets)
    {
        var falloff = VirtualActionModel.AoeActualTargetFalloff(actionData, actualTargets);
        if (Math.Abs(falloff - 1f) < 0.001f)
            return actionData;

        var scaled = CloneActionData(actionData);
        scaled.Potency = Math.Max(1, (int)MathF.Round(scaled.Potency * falloff));
        if (scaled.ComboPotency > 0)
            scaled.ComboPotency = Math.Max(1, (int)MathF.Round(scaled.ComboPotency * falloff));
        return scaled;
    }

    /// <summary>Engage a still-idle selected enemy (mirrors the sim-mode first-hit engage).</summary>
    private void EngageIdleTarget(uint entityId)
    {
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.SimulatedEntityId != entityId || npc.AiState != Ai.NpcAiState.Idle)
                continue;
            npc.AiState = Ai.NpcAiState.Engaging;
            npc.EngageDelayTimer = Ai.NpcAiController.PlayerTriggeredEngageDelay;
            Ai.NpcAiController.StaggerTimers(npc);
            AddLogEntry($"{npc.Name} engages!", CombatLogType.Info);
            break;
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

    private static bool TrySpendMp(SimulatedEntityState entity, ActionData actionData, out string? failReason)
    {
        failReason = null;
        var cost = Math.Max(0, actionData.MpCost);
        if (cost <= 0)
            return true;

        if (entity.CurrentMp < cost)
        {
            failReason = $"Not enough MP ({entity.CurrentMp}/{cost}).";
            return false;
        }

        entity.CurrentMp = Math.Max(0, entity.CurrentMp - cost);
        return true;
    }

    private static void RestoreMp(SimulatedEntityState entity, int amount)
    {
        if (amount <= 0 || entity.MaxMp <= 0)
            return;

        entity.CurrentMp = Math.Min(entity.MaxMp, entity.CurrentMp + amount);
    }

    public void RegisterNpcEntity(SimulatedNpc npc)
    {
        State.Entities[npc.SimulatedEntityId] = npc.State;
    }

    /// <summary>
    /// Free a slot in a full map-enemy pool by evicting the earliest-joined map
    /// enemy that is already dead. The evicted enemy is fully recovered: its
    /// ragdoll is cleared (if it was the ragdolled corpse), death animation reset,
    /// mode restored, and the real BattleNpc returned to its original map position
    /// and object kind. Returns true if an enemy was evicted, false when every map
    /// enemy is still alive (the pool is genuinely full and the newcomer is refused).
    /// </summary>
    public bool TryEvictDeadMapEnemy()
    {
        SimulatedNpc? victim = null;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            // Map enemies are real BattleNpcs (not client-controlled). Iteration is
            // in join order, so the first dead one found is the earliest joined.
            if (npc.IsClientControlled || npc.IsAlive)
                continue;
            victim = npc;
            break;
        }

        if (victim == null)
            return false;

        UnregisterNpcEntity(victim.SimulatedEntityId);

        unsafe
        {
            if (victim.BattleChara != null)
            {
                // Stop physics-driving the corpse before its position is restored.
                if (ragdollController.IsActive && ragdollController.TargetCharacterAddress == victim.Address)
                    ragdollController.Deactivate();

                animationController.ResetDeathAnimation(victim.BattleChara);
                animationController.ClearBattleStance(victim);
                var character = (Character*)victim.BattleChara;
                character->Mode = CharacterModes.Normal;
                character->ModeParam = 0;
            }
        }

        // Restores original position, ObjectKind/SubKind and clears the NPC's target.
        npcSelector.DeselectNpc(victim);

        AddLogEntry($"{victim.Name} leaves the fight.", CombatLogType.Info);
        log.Info($"Evicted dead map enemy '{victim.Name}' to make room for a new one.");
        return true;
    }

    public void RegisterCompanionEntity(CombatCompanion companion)
    {
        State.Entities[companion.SimulatedEntityId] = companion.State;
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
