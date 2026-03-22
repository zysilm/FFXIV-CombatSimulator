# Spec: Combat Simulation Engine

## Overview
The combat engine processes all combat interactions between the player and
simulated NPCs. It handles damage calculation, action validation, cooldown
tracking, combo chains, and produces ActionEffect data for the animation
pipeline.

## Public Interface

```csharp
public class CombatEngine : IDisposable
{
    /// Start combat simulation mode.
    void StartSimulation();

    /// Stop combat simulation mode. Resets all state.
    void StopSimulation();

    /// Per-frame update. Called from Framework.Update.
    void Tick(float deltaTime);

    /// Process a player action (called from UseAction hook).
    SimulatedActionResult ProcessPlayerAction(
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam);

    /// Process an NPC action (called from NPC AI).
    SimulatedActionResult ProcessNpcAction(
        SimulatedNpc source,
        uint actionId,
        ulong targetId);

    /// Current simulation state.
    SimulationState State { get; }

    /// Is simulation currently active?
    bool IsActive { get; }
}
```

## SimulationState

```csharp
public class SimulationState
{
    // Entity states (keyed by simulated entity ID)
    public Dictionary<uint, SimulatedEntityState> Entities { get; }

    // Player's simulated state (separate from real game state)
    public SimulatedEntityState PlayerState { get; }

    // Global state
    public bool IsActive { get; set; }
    public float SimulationTime { get; set; }  // Total elapsed time

    // Combat stats
    public long TotalDamageDealt { get; set; }
    public long TotalDamageTaken { get; set; }
    public long TotalHealingDone { get; set; }
    public float CombatDuration { get; set; }
}
```

### SimulatedEntityState

```csharp
public class SimulatedEntityState
{
    // Identity
    public uint EntityId { get; set; }
    public string Name { get; set; }
    public bool IsPlayer { get; set; }

    // Vitals
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int CurrentMp { get; set; }
    public int MaxMp { get; set; }
    public int Level { get; set; }
    public bool IsAlive => CurrentHp > 0;

    // Stats (for damage calculation)
    public int MainStat { get; set; }       // STR/DEX/INT/MND based on job
    public int Determination { get; set; }
    public int CriticalHit { get; set; }
    public int DirectHit { get; set; }
    public int Defense { get; set; }
    public int MagicDefense { get; set; }
    public int AttackPower { get; set; }
    public int AttackMagicPotency { get; set; }

    // Combat state
    public float AnimationLock { get; set; }   // Countdown timer
    public float GcdRemaining { get; set; }    // GCD recast remaining
    public float AutoAttackTimer { get; set; } // Time until next auto-attack
    public bool IsCasting { get; set; }
    public float CastTimeElapsed { get; set; }
    public float CastTimeTotal { get; set; }
    public uint CastActionId { get; set; }
    public ulong CastTargetId { get; set; }

    // Combo
    public uint LastComboAction { get; set; }
    public float ComboTimer { get; set; }      // 30s window

    // Cooldowns
    public Dictionary<int, RecastState> Cooldowns { get; }

    // Status effects
    public List<SimulatedStatus> StatusEffects { get; }
}

public class RecastState
{
    public uint ActionId { get; set; }
    public float Elapsed { get; set; }
    public float Total { get; set; }
    public bool IsActive => Elapsed < Total;
    public float Remaining => Total - Elapsed;
}

public class SimulatedStatus
{
    public uint StatusId { get; set; }
    public float Duration { get; set; }
    public float Remaining { get; set; }
    public int Stacks { get; set; }
    public uint SourceId { get; set; }
    public float Potency { get; set; }  // For DoTs/HoTs
}
```

## Action Processing Pipeline

When the player uses an ability (captured by UseAction hook):

```
1. VALIDATE action
   ├── Is simulation active? (if not, pass through to original)
   ├── Is target a simulated NPC? (if not, pass through)
   ├── Is player alive? (if not, reject)
   ├── Is player animation-locked? (if yes, queue or reject)
   ├── Is action on cooldown? (if yes, queue or reject)
   ├── Does player have required resources? (MP check)
   └── Is combo valid? (combo chain check)

2. CALCULATE damage/healing
   ├── Look up action data (potency, type, element)
   ├── Read player stats (from real BattleChara)
   ├── Read target stats (from SimulatedEntityState)
   ├── Apply damage formula
   ├── Roll for critical hit
   ├── Roll for direct hit
   ├── Apply buffs/debuffs modifiers
   └── Clamp to valid range

3. APPLY results
   ├── Subtract HP from target
   ├── Apply status effects (from action data)
   ├── Update combo state
   ├── Set animation lock
   ├── Start cooldown
   ├── Consume resources (MP)
   └── Check death condition

4. TRIGGER visuals
   ├── Fabricate ActionEffect data
   ├── Call ActionEffectHandler.Receive()
   └── (Animations, VFX, flytext happen automatically)

5. RETURN result
   └── SimulatedActionResult with damage, crit, effects applied
```

## Damage Calculation

### FFXIV Damage Formula (Simplified)

```
Base Damage:
  D1 = ⌊ Potency × f(MainStat) × f(Determination) / 100 ⌋
  D2 = ⌊ D1 × f(TenacityOrSpeed) / 1000 ⌋
  D3 = ⌊ D2 × f(TraitModifier) / 100 ⌋

Where:
  f(MainStat) = ⌊ 195 × (MainStat - LevelModBase) / LevelModDiv + 100 ⌋
  f(Determination) = ⌊ 140 × (Det - LevelModBase) / LevelModDiv + 1000 ⌋

Critical Hit:
  CritRate = ⌊ 200 × (CritStat - LevelModBase) / LevelModDiv + 50 ⌋ / 1000
  CritMultiplier = ⌊ 200 × (CritStat - LevelModBase) / LevelModDiv + 1400 ⌋ / 1000

Direct Hit:
  DHRate = ⌊ 550 × (DHStat - LevelModBase) / LevelModDiv ⌋ / 1000
  DHMultiplier = 1.25

Defense Mitigation:
  DefMitigation = ⌊ 15 × LevelModDiv / Defense + 85 ⌋ / 100

Variance:
  RNG = random(0.95, 1.05)

Final = ⌊ D3 × DefMitigation × CritMult? × DHMult? × RNG ⌋
```

### Level Mod Table (Key Levels)

| Level | Base (Main) | Base (Sub) | Div |
|-------|-------------|------------|-----|
| 80 | 340 | 380 | 1900 |
| 90 | 390 | 400 | 1900 |
| 100 | 440 | 420 | 2780 |

(Full table to be populated from game data)

### Implementation

```csharp
public class DamageCalculator
{
    /// Calculate damage for a single action hit.
    DamageResult Calculate(
        SimulatedEntityState source,
        SimulatedEntityState target,
        ActionData actionData);
}

public class DamageResult
{
    public int Damage { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public DamageType Type { get; set; }  // Physical, Magical, Unique
}
```

## Action Data

Action properties read from Lumina game data:

```csharp
public class ActionData
{
    public uint ActionId { get; set; }
    public string Name { get; set; }
    public int Potency { get; set; }
    public float CastTime { get; set; }        // 0 = instant
    public float RecastTime { get; set; }       // GCD or oGCD cooldown
    public int RecastGroup { get; set; }        // Cooldown group
    public float Range { get; set; }            // Max range in yalms
    public float Radius { get; set; }           // AoE radius (0 = single target)
    public DamageType DamageType { get; set; }
    public int MpCost { get; set; }
    public bool IsComboAction { get; set; }
    public uint ComboFrom { get; set; }         // Previous action in combo
    public int ComboPotency { get; set; }       // Potency when used in combo
    public uint[] AppliedStatuses { get; set; } // Status effects applied
    public float AnimationLock { get; set; }    // ~0.6s for most actions
}
```

Read from Lumina sheets:
- `Action` sheet → potency, cast time, recast, range, combo info
- `ActionTransient` sheet → description text
- `Status` sheet → status effect data

## Cooldown System

```csharp
public class CooldownTracker
{
    /// Start a cooldown for a recast group.
    void StartCooldown(int recastGroup, uint actionId, float duration);

    /// Check if a recast group is on cooldown.
    bool IsOnCooldown(int recastGroup);

    /// Get remaining time for a recast group.
    float GetRemaining(int recastGroup);

    /// Tick all cooldowns by deltaTime.
    void Tick(float deltaTime);
}
```

- GCD actions share recast group 58 (2.5s base, modified by speed)
- oGCD actions have individual recast groups
- Some actions share recast groups (e.g., Aetherflow skills)

## Combo System

```csharp
public class ComboTracker
{
    /// Record that an action was used, update combo state.
    void RecordAction(uint actionId);

    /// Check if actionId is a valid continuation of the current combo.
    bool IsComboValid(uint actionId);

    /// Get the current combo potency bonus for an action.
    int GetComboPotency(uint actionId);

    /// Tick the combo timer. Combo expires after 30s.
    void Tick(float deltaTime);
}
```

## Auto-Attack System

```
Every entity has an auto-attack timer.
Player auto-attack delay: ~2.56s (based on weapon delay)
NPC auto-attack delay: ~3.0s

On tick:
  autoAttackTimer -= deltaTime
  if autoAttackTimer <= 0 && hasTarget && inRange:
    ProcessAutoAttack(source, target)
    autoAttackTimer = autoAttackDelay
```

## NPC Damage Formula

NPC auto-attacks and skills use a separate formula (`CalculateNpcAutoAttack`)
with quadratic level scaling for more impactful damage at high levels:

```
baseDamage = potency × (1.0 + npcLevel × 0.1 + npcLevel² × 0.002)

levelDiff = npcLevel - targetLevel
levelMultiplier = 1.0 + levelDiff × 0.08
  + (if levelDiff > 0: levelDiff² × 0.005)   // extra quadratic bonus
levelMultiplier = max(0.2, levelMultiplier)

defense mitigation = 2000 / (2000 + defense)    // softened curve
variance = random(0.90, 1.10)

finalDamage = baseDamage × levelMultiplier × mitigation × variance
```

This ensures a level 100 NPC vs a level 70 player deals lethal damage within
~30 seconds, which feels combat-appropriate.

## Death Animation Delay

When an entity's HP reaches 0, death is not applied immediately. Instead, it
is queued into a `pendingDeaths` list with a 500ms timer. This allows the
killing blow's attack animation to fully play before the death animation starts.

```csharp
private readonly List<PendingDeath> pendingDeaths = new();
private const float DeathAnimationDelay = 0.5f; // 500ms

private struct PendingDeath
{
    public ulong EntityId;
    public bool IsPlayer;
    public float Timer;
}
```

Each frame, `TickPendingDeaths(deltaTime)` decrements the timer. When it
expires, `ExecuteDeathAnimation()` triggers the actual death animation and
state changes. Pending deaths are cleared on simulation start and reset.

## Aggro Propagation

When a target is first engaged (transitions to combat), nearby idle targets
can automatically join the fight. Controlled by configuration:

- `EnableAggroPropagation` (bool) — toggle on/off
- `AggroPropagationRange` (float) — maximum distance in yalms

```
On NPC engage:
  if config.EnableAggroPropagation:
    for each idle NPC within AggroPropagationRange of the engaged NPC:
      set AiState = Combat
      log "{name} joins the fight!"
```

## Cast Bar Simulation

```
On action with CastTime > 0:
  1. Set IsCasting = true
  2. Set CastTimeTotal = action.CastTime (modified by speed stats)
  3. Each frame: CastTimeElapsed += deltaTime
  4. When CastTimeElapsed >= CastTimeTotal:
     - IsCasting = false
     - Execute the action (damage calc, effects)
  5. If interrupted:
     - IsCasting = false
     - CastTimeElapsed = 0
     - No action executed
```

## SimulatedActionResult

```csharp
public class SimulatedActionResult
{
    public bool Success { get; set; }
    public string FailReason { get; set; }  // null if success
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public bool IsCombo { get; set; }
    public uint ActionId { get; set; }
    public ulong SourceId { get; set; }
    public ulong TargetId { get; set; }
    public List<uint> AppliedStatuses { get; set; }
    public bool TargetKilled { get; set; }
}
```
