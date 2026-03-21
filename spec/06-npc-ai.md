# Spec: NPC AI Controller

## Overview
Controls simulated NPC behavior during combat: targeting, movement,
auto-attacks, skill usage, and death state. Each NPC runs a simple
state machine updated every frame.

## Public Interface

```csharp
public class NpcAiController
{
    /// Update all NPC AI states. Called each frame.
    void Tick(float deltaTime);

    /// Engage an NPC (player attacked it).
    void EngageNpc(SimulatedNpc npc, ulong attackerId);

    /// Disengage an NPC (reset to idle).
    void DisengageNpc(SimulatedNpc npc);

    /// Register AI behavior for an NPC.
    void SetBehavior(SimulatedNpc npc, NpcBehavior behavior);
}
```

## NPC State Machine

```
        ┌────────────────┐
        │     IDLE       │
        │  (standing)    │
        └───────┬────────┘
                │ player attacks
                ▼
        ┌────────────────┐
        │   ENGAGING     │◄──────────┐
        │  (turning to   │           │
        │   face player) │           │ target re-enters range
        └───────┬────────┘           │
                │ facing complete    │
                ▼                    │
        ┌────────────────┐           │
        │   COMBAT       │───────────┤
        │  (attacking)   │           │ target out of range
        └───────┬────────┘           │ (chase or disengage)
                │ HP reaches 0      │
                ▼                    │
        ┌────────────────┐           │
        │     DEAD       │           │
        │  (death anim)  │           │
        └────────────────┘           │
                                     │
        ┌────────────────┐           │
        │   CHASING      │───────────┘
        │  (moving to    │
        │   player)      │──── leash distance exceeded
        └────────────────┘        │
                                  ▼
                          ┌────────────────┐
                          │   RESETTING    │
                          │  (return to    │
                          │   spawn point) │──→ IDLE
                          └────────────────┘
```

### State Definitions

```csharp
public enum NpcAiState
{
    Idle,       // Standing at position, not in combat
    Engaging,   // Turning to face attacker
    Combat,     // Actively fighting (auto-attacks + skills)
    Chasing,    // Moving toward target (out of melee range)
    Dead,       // Death animation playing, awaiting cleanup/respawn
    Resetting,  // Returning to spawn point after leash
}
```

## NPC Behavior Templates

```csharp
public class NpcBehavior
{
    // Basic properties
    public float AutoAttackDelay { get; set; } = 3.0f;  // Seconds between auto-attacks
    public float AutoAttackRange { get; set; } = 3.0f;  // Melee range in yalms
    public float AggroRange { get; set; } = 0.0f;       // 0 = no aggro on sight
    public float LeashDistance { get; set; } = 40.0f;    // Reset if player goes beyond
    public float MoveSpeed { get; set; } = 6.0f;        // Yalms per second

    // Skills
    public List<NpcSkill> Skills { get; set; } = new();

    // Auto-attack
    public int AutoAttackPotency { get; set; } = 110;
    public uint AutoAttackActionId { get; set; } = 7;   // Generic auto-attack
}

public class NpcSkill
{
    public uint ActionId { get; set; }
    public string Name { get; set; }
    public int Potency { get; set; }
    public float Cooldown { get; set; }         // Seconds
    public float CastTime { get; set; }         // 0 = instant
    public float Range { get; set; }            // Max range
    public float Radius { get; set; }           // AoE radius (0 = single target)
    public bool Interruptible { get; set; } = true;
    public int Priority { get; set; } = 0;      // Higher = used first when available
    public float HpThreshold { get; set; } = 1.0f; // Only use below this HP% (1.0 = always)
}
```

## Preset Behaviors

### Training Dummy
```csharp
new NpcBehavior
{
    AutoAttackDelay = float.MaxValue,  // Never attacks
    Skills = new(),                     // No skills
    LeashDistance = float.MaxValue,     // Never resets
}
```

### Basic Melee Enemy
```csharp
new NpcBehavior
{
    AutoAttackDelay = 3.0f,
    AutoAttackRange = 3.0f,
    AutoAttackPotency = 110,
    MoveSpeed = 6.0f,
    LeashDistance = 40.0f,
    Skills = new()
    {
        new NpcSkill
        {
            Name = "Heavy Swing",
            Potency = 200,
            Cooldown = 15.0f,
            CastTime = 2.0f,
            Range = 3.0f,
        }
    }
}
```

### Basic Ranged Enemy
```csharp
new NpcBehavior
{
    AutoAttackDelay = 3.0f,
    AutoAttackRange = 25.0f,
    AutoAttackPotency = 80,
    MoveSpeed = 5.0f,
    LeashDistance = 40.0f,
    Skills = new()
    {
        new NpcSkill
        {
            Name = "Fire",
            Potency = 300,
            Cooldown = 8.0f,
            CastTime = 2.5f,
            Range = 25.0f,
        },
        new NpcSkill
        {
            Name = "Thunder",
            Potency = 150,
            Cooldown = 30.0f,
            CastTime = 0f,
            Range = 25.0f,
        }
    }
}
```

## Per-Frame AI Logic

```
Tick(npc, deltaTime):
  switch (npc.AiState):

    case Idle:
      // Do nothing. Wait for EngageNpc() call.
      break

    case Engaging:
      // Smoothly rotate to face the target
      targetRotation = atan2(target.x - npc.x, target.z - npc.z)
      npc.rotation = LerpAngle(npc.rotation, targetRotation, deltaTime * 5.0)
      if (AngleDiff < 0.1):
        npc.AiState = Combat
      break

    case Combat:
      // 1. Face target
      UpdateRotation(npc, target)

      // 2. Check range
      distance = Distance(npc.position, target.position)
      if (distance > autoAttackRange + 1.0):
        npc.AiState = Chasing
        break

      // 3. Check leash
      if (Distance(npc.position, npc.spawnPosition) > leashDistance):
        npc.AiState = Resetting
        break

      // 4. Try skills (highest priority first)
      foreach skill in npc.behavior.Skills.OrderByDescending(s => s.Priority):
        if (skill.CooldownRemaining <= 0 &&
            distance <= skill.Range &&
            npc.state.CurrentHp / npc.state.MaxHp <= skill.HpThreshold):
          ExecuteNpcSkill(npc, skill, target)
          break

      // 5. Auto-attack
      npc.autoAttackTimer -= deltaTime
      if (npc.autoAttackTimer <= 0):
        ExecuteAutoAttack(npc, target)
        npc.autoAttackTimer = npc.behavior.AutoAttackDelay
      break

    case Chasing:
      // Move toward target
      direction = Normalize(target.position - npc.position)
      npc.position += direction * moveSpeed * deltaTime
      UpdateNpcPosition(npc)  // Write to game object

      // Check if in range
      if (Distance(npc.position, target.position) <= autoAttackRange):
        npc.AiState = Combat

      // Check leash
      if (Distance(npc.position, npc.spawnPosition) > leashDistance):
        npc.AiState = Resetting
      break

    case Dead:
      // Wait for respawn command or timeout
      npc.deadTimer += deltaTime
      break

    case Resetting:
      // Move back to spawn point
      direction = Normalize(npc.spawnPosition - npc.position)
      npc.position += direction * moveSpeed * 1.5 * deltaTime
      UpdateNpcPosition(npc)

      // Restore HP while resetting
      npc.state.CurrentHp = npc.state.MaxHp

      if (Distance(npc.position, npc.spawnPosition) < 0.5):
        npc.position = npc.spawnPosition
        npc.AiState = Idle
      break
```

## NPC Position Updates

NPC position is written directly to the game object's Position field:

```csharp
unsafe void UpdateNpcPosition(SimulatedNpc npc)
{
    if (npc.BattleChara == null) return;

    var gameObj = (GameObject*)npc.BattleChara;
    gameObj->Position = npc.SimulatedPosition;
    gameObj->Rotation = npc.SimulatedRotation;

    // Note: This only updates the client-side object.
    // Since it's in index 200+, no position sync packets are sent.
}
```

## NPC Cast System

When an NPC uses a skill with CastTime > 0:

```
1. NPC enters casting state
   - IsCasting = true
   - CastTimeTotal = skill.CastTime
   - CastTimeElapsed = 0
   - Display cast bar (write to CastInfo on BattleChara or ImGui overlay)

2. Each frame while casting:
   - CastTimeElapsed += deltaTime
   - Update cast bar progress
   - NPC cannot auto-attack or use other skills while casting
   - NPC continues to face target

3. When cast completes:
   - Execute the skill (damage calculation, ActionEffect)
   - IsCasting = false
   - Start skill cooldown

4. If player interrupts (via interrupt ability):
   - IsCasting = false
   - Skill goes on partial cooldown (or full cooldown)
   - NPC flinches (hit reaction animation)
```

## NPC Skill Execution

```csharp
void ExecuteNpcSkill(SimulatedNpc npc, NpcSkill skill, SimulatedEntityState target)
{
    if (skill.CastTime > 0)
    {
        // Start casting
        npc.State.IsCasting = true;
        npc.State.CastActionId = skill.ActionId;
        npc.State.CastTimeTotal = skill.CastTime;
        npc.State.CastTimeElapsed = 0;
        npc.State.CastTargetId = target.EntityId;
        return; // Will execute when cast completes
    }

    // Instant skill
    var result = combatEngine.ProcessNpcAction(npc, skill.ActionId, target.EntityId);

    // Start cooldown
    skill.CooldownRemaining = skill.Cooldown;

    // Animation lock
    npc.State.AnimationLock = 0.6f;
}
```

## NPC Death

When NPC HP reaches 0:
```
1. Set NPC AI state to Dead
2. Play death animation (CharacterMode = Dead)
3. Make NPC untargetable (clear TargetableStatus flags)
4. Stop all NPC actions
5. Optional: despawn after 10 seconds
6. Optional: respawn button in UI
```

## Constraints

- NPC movement is client-side only (position written to game object directly)
- NPC movement does not respect navmesh (walks in straight lines)
  - Phase 7 stretch goal: integrate with vnavmesh for pathfinding
- NPCs cannot use ground-targeted abilities (Phase 7 stretch goal)
- Maximum 3 simultaneous active combatant NPCs (performance consideration)
