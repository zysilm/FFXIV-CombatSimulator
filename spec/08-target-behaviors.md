# Spec: Target Behaviors — Target Approach

## Overview
The Target Approach feature moves active combat target NPCs (enemies) to stay
near the player's character position. This is a client-side visual manipulation
that allows the player to fight NPCs that would otherwise be too far away,
without needing to walk to them.

After player death or combat reset, approached NPCs remain at their current
position near the player — they do not return to their original spawn location.

## User-Facing Behavior

1. Player enables "Move Targets Near Player" in the Target Behaviors section
2. Player sets the desired approach distance (1–30 yalms, default 3.0)
3. Player selects NPCs as combat targets (via normal targeting + attack)
4. Targeted NPCs smoothly move toward the player, stopping at the configured distance
5. NPCs face the player while in approach range
6. When the player dies (simulated), NPCs freeze in place — they do not leave
7. When combat is reset, NPCs stay at their current position and are ready to fight again
8. When simulation is stopped entirely, NPCs are deselected and normal game control resumes

## Configuration

```csharp
// In Configuration.cs
public bool EnableTargetApproach { get; set; } = false;
public float TargetApproachDistance { get; set; } = 3.0f;  // yalms
```

## Architecture

### Components Involved

1. **NpcAiController** — Owns the approach tick logic
2. **MovementBlockHook** — Blocks server-driven position updates for approached NPCs
3. **Configuration** — Stores enable flag and distance
4. **MainWindow** — GUI controls (toggle + slider)

### Data Flow

```
Each Frame (when approach enabled):
  NpcAiController.Tick()
    ├── Normal AI tick for each NPC (combat, skills, auto-attacks)
    ├── For each active NPC:
    │   ├── Register NPC address in MovementBlockHook (block server updates)
    │   ├── Suppress Resetting state → force to Idle
    │   └── TickApproach():
    │       ├── Skip if NPC is dead
    │       ├── Skip if player is dead (freeze in place)
    │       ├── Calculate target position (approachDistance from player)
    │       ├── Smooth linear movement toward target position
    │       ├── Write position directly to GameObject->Position
    │       └── Force rotation toward player
    └── When disabled: ClearApproachNpcs() from MovementBlockHook
```

## Position Calculation

```
Given:
  playerPos = local player world position
  npcPos    = current NPC world position
  targetDist = configuration approach distance

Target position calculation:
  if distance(npcPos, playerPos) < 0.1:
    // NPC is right on top of player — place in front
    forward = (-sin(playerRotation), 0, -cos(playerRotation))
    targetPos = playerPos + forward * targetDist
  else:
    // Maintain current angle from player
    dirFromPlayer = normalize(npcPos - playerPos)
    targetPos = playerPos + dirFromPlayer * targetDist

  // Approximate terrain following
  targetPos.Y = playerPos.Y

Movement:
  speed = npc.MoveSpeed * 1.5  (or 8.0 if no behavior speed)
  move = normalize(targetPos - npcPos) * speed * deltaTime
  // Clamp to prevent overshoot
  if distance(npcPos, targetPos) < moveDist:
    newPos = targetPos
  else:
    newPos = npcPos + move
```

## Position Blocking (MovementBlockHook)

When the approach feature moves a real NPC (not client-spawned), the game's
server may attempt to update the NPC's position through `SetPosition`. This
would cause visual flickering as the server and our approach fight each other.

To prevent this, we register approached NPC addresses in `MovementBlockHook`:

```csharp
// In NpcAiController, during approach tick:
if (!npc.IsClientControlled)
    movementBlockHook.AddApproachNpc(npc.Address);

// In SetPositionDetour:
if (approachBlockedAddresses.Contains((nint)thisPtr))
    return; // Skip server position update
```

Our direct memory writes (`gameObj->Position = newPos`) bypass the SetPosition
hook entirely, so they are never blocked.

Cleanup: `ClearApproachNpcs()` is called when approach is disabled, simulation
stops, or on dispose.

## Interaction with AI State Machine

When approach is active, several AI behaviors are modified:

| AI Behavior | Normal | With Approach Active |
|-------------|--------|---------------------|
| Leash distance check | Triggers Resetting state | Suppressed |
| Resetting state | NPC walks back to spawn | Forced back to Idle |
| Chasing state | NPC moves toward player | Approach handles positioning |
| Combat range check | Switch to Chasing if out of range | NPC always in range |
| Player death | NPCs may go Idle/Reset | NPCs freeze at current position |
| Combat reset | AI state unchanged | AI state reset to Idle, position kept |

## GUI

Located in the main window as "Target Behaviors" CollapsingHeader, placed
between "Active Targets" and "Simulation Controls":

```
── Target Behaviors ─────────────────────────────
│ ☑ Move Targets Near Player                    │
│                                                │
│ Active targets will move to stay near your     │
│ character. On death or reset, they remain.     │
│                                                │
│ Approach Distance: [──●──] 3.0 yalms          │
──────────────────────────────────────────────────
```

- Checkbox enables/disables the feature (persisted in config)
- Distance slider only shown when feature is enabled
- Slider range: 1.0–30.0 yalms, step 0.1
- Changes take effect immediately (next frame)

## NPC Spread Algorithm

When multiple NPCs are active, they are distributed across a 240-degree arc
centered on the player's forward direction to prevent stacking:

```csharp
float arcSpan = MathF.PI * 4f / 3f; // 240 degrees
float angleStep = totalNpcs > 1 ? arcSpan / (totalNpcs - 1) : 0;
float startAngle = playerRot - arcSpan / 2f;
float angle = startAngle + angleStep * npcIndex;
var dir = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
targetPos = playerPos + dir * targetDist;
```

Each NPC is assigned a unique angular slot based on its index, ensuring they
are evenly spaced and never stack on the same coordinate.

## Aggro Propagation

When `EnableAggroPropagation` is true, engaging one target causes nearby idle
targets within `AggroPropagationRange` yalms to automatically join combat.

```
── Target Behaviors ─────────────────────────────
│ ☑ Move Targets Near Player                    │
│ Approach Distance: [──●──] 3.0 yalms          │
│                                                │
│ ☑ Aggro Propagation                           │
│ Aggro Range: [──●──] 15.0 yalms               │
│ Nearby idle targets join combat when one is    │
│ attacked.                                      │
──────────────────────────────────────────────────
```

## Edge Cases

- **Multiple NPCs**: NPCs are spread across a 240° arc (see above). No stacking.
- **NPC on top of player**: Falls back to placing NPC in front of the player.
- **Terrain height**: Approximated using player Y coordinate. This works well on
  flat terrain but may cause floating/clipping on slopes or multi-level areas.
- **Feature toggled off mid-combat**: NPC positions stop being overridden.
  Server may snap them back to their server-side position. SetPosition block
  is cleared immediately.
- **Simulation stopped**: All approach state is cleaned up. NPCs are deselected
  and ObjectKind/SubKind restored. Normal game control resumes.

## Future Enhancements

- **Navmesh pathfinding**: Integrate with [vnavmesh](https://github.com/awgil/ffxiv_navmesh)
  or DotRecast for terrain-aware pathfinding instead of linear movement.
  Would prevent NPCs from walking through walls or floating over terrain.
- **Per-NPC toggle**: Allow enabling/disabling approach per individual target.
- **Approach speed setting**: Configurable movement speed for approach animation.

## Safety Notes

- All position changes are client-side only (direct memory writes to game objects)
- Server-side NPC positions are never modified
- No network traffic is generated
- Position hook blocking is defensive only (prevents server overwriting our visuals)
- All state is cleaned up on plugin dispose
