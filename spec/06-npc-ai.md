# Spec: NPC AI Controller

## Overview
Controls simulated NPC behavior during combat: targeting, movement,
auto-attacks, skill usage, death state, and target approach positioning.
Each NPC runs a simple state machine updated every frame.

## Public Interface

```csharp
public unsafe class NpcAiController : IDisposable
{
    NpcAiController(CombatEngine, AnimationController, MovementBlockHook,
                    IClientState, Configuration, IPluginLog);

    /// Update all NPC AI states + target approach. Called each frame.
    void Tick(float deltaTime, IReadOnlyList<SimulatedNpc> npcs);

    /// Engage an NPC (player attacked it).
    void EngageNpc(SimulatedNpc npc);

    /// Clear approach positioning state. Called on simulation stop.
    void ClearApproachState();
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
    Chasing,    // Moving toward target (client-controlled NPCs only)
    Dead,       // Death animation playing
    Resetting,  // Returning to spawn point (suppressed when approach is active)
}
```

## Real NPCs vs Client-Controlled NPCs

The AI handles two categories of NPCs differently:

| Behavior | Real NPC (`IsClientControlled=false`) | Client-Controlled |
|----------|--------------------------------------|-------------------|
| Movement | No movement (server controls) | Chasing/Resetting states |
| Rotation | No rotation (unless approach active) | Smooth lerp to face player |
| Range check | Generous (+30 yalm buffer) | Normal (+1 yalm) |
| Leash reset | Instant HP restore + Idle | Walk back to spawn |
| Target approach | Position override via direct write | Position override via direct write |

## NPC Behavior Templates

```csharp
public enum NpcBehaviorType { TrainingDummy, BasicMelee, BasicRanged, Boss }

public class NpcBehavior
{
    float AutoAttackDelay = 3.0f;
    float AutoAttackRange = 3.0f;
    int   AutoAttackPotency = 110;
    uint  AutoAttackActionId = 7;
    float LeashDistance = 40.0f;
    float MoveSpeed = 6.0f;
    List<NpcSkill> Skills;
}
```

### Presets

- **TrainingDummy**: Never attacks, infinite leash
- **BasicMelee**: 3s auto-attack, "Heavy Swing" skill (2s cast, 15s CD)
- **BasicRanged**: 25y range, "Fire" (2.5s cast) + "Thunder" (instant)
- **Boss**: Fast autos, "Mighty Strike" + "Whirlwind" + "Enrage" (below 30% HP)

## Per-Frame AI Logic

```
Tick(npc, deltaTime):
  1. Tick skill cooldowns
  2. Check death condition
  3. Switch on AiState:
     - Idle: do nothing
     - Engaging: rotate → transition to Combat
     - Combat: face target, check leash, check range, try skills, auto-attack
     - Chasing: move toward player (client-controlled only)
     - Dead: increment dead timer
     - Resetting: move back to spawn, restore HP
```

## Combat State Details

In **Combat** state each frame:
1. Face the player (client-controlled only, or if approach active)
2. Check leash distance (suppressed when target approach is active)
3. Check attack range (generous for real NPCs)
4. If player is dead → go Idle
5. Handle active cast (progress cast bar, resolve on completion)
6. Check animation lock
7. Try skills (highest priority first, check cooldown + range + HP threshold)
8. Auto-attack on timer

## Target Approach System

See [spec/08-target-behaviors.md](08-target-behaviors.md) for full details.

When `Configuration.EnableTargetApproach` is true, after the normal AI tick:

1. All active NPCs (real and client-controlled) are moved toward the player
2. NPCs stop at `TargetApproachDistance` yalms from the player
3. NPCs are forced to face the player (even real NPCs)
4. Server-driven position updates are blocked via `MovementBlockHook`
5. Leash/reset behaviors are suppressed
6. On player death, NPCs freeze in place (stay near player)
7. On combat reset, NPCs stay at their current position

## NPC Position Updates

```csharp
// Client-controlled NPCs: direct write via SetNpcPosition
var gameObj = (GameObject*)npc.BattleChara;
gameObj->Position = newPos;

// Approach-controlled NPCs: direct write (same mechanism)
// Server SetPosition calls blocked by MovementBlockHook
gameObj->Position = approachPos;
```

## NPC Cast System

When an NPC uses a skill with CastTime > 0:
1. Set `IsCasting = true`, start tracking elapsed time
2. Each frame: progress cast, NPC cannot auto-attack or use other skills
3. On completion: execute skill (damage + animation), start cooldown
4. On interruption: cancel cast, no action executed

## NPC Death

When NPC HP reaches 0:
1. Set AI state to Dead
2. Death animation triggered by AnimationController
3. NPC stops all actions
4. On combat reset: AI state returns to Idle, HP restored, death animation cleared
