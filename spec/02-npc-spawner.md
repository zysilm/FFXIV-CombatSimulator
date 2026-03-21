# Spec: NPC Spawner

## Overview
Manages spawning, configuration, and despawning of client-side battle NPCs
using the game's ClientObjectManager. All NPCs use client-side object indexes
(200-448) which are invisible to the server.

## Public Interface

```csharp
public class NpcSpawner : IDisposable
{
    /// Spawn an NPC near the player.
    /// Returns a handle to the spawned NPC, or null on failure.
    SimulatedNpc? SpawnNpc(NpcSpawnRequest request);

    /// Despawn a specific NPC by handle.
    void DespawnNpc(SimulatedNpc npc);

    /// Despawn all spawned NPCs.
    void DespawnAll();

    /// List of currently spawned NPCs.
    IReadOnlyList<SimulatedNpc> SpawnedNpcs { get; }

    /// Maximum NPCs that can be spawned simultaneously.
    int MaxNpcs => 10;
}
```

## Data Types

### NpcSpawnRequest
```csharp
public class NpcSpawnRequest
{
    public uint BNpcNameId { get; set; }      // From BNpcName sheet (display name)
    public uint BNpcBaseId { get; set; }       // From BNpcBase sheet (model/stats)
    public int Level { get; set; } = 90;       // NPC level
    public float HpMultiplier { get; set; } = 1.0f;
    public Vector3? Position { get; set; }     // null = spawn near player
    public float? Rotation { get; set; }       // null = face player
}
```

### SimulatedNpc
```csharp
public class SimulatedNpc
{
    // Identity
    public uint SimulatedEntityId { get; }     // Our unique ID (not a real entity ID)
    public int ObjectIndex { get; }            // Client object table index (200+)
    public string Name { get; }

    // Game object reference
    public unsafe BattleChara* BattleChara { get; }
    public nint Address { get; }

    // Simulation state (managed by CombatEngine, not game memory)
    public SimulatedEntityState State { get; }

    // Lifecycle
    public bool IsAlive => State.CurrentHp > 0;
    public bool IsSpawned { get; }
}
```

## Spawning Process

### Step 1: Find Available Index
```
Call ClientObjectManager.CalculateNextAvailableIndex()
  → returns index in range [200, 448]
  → if no index available, return null (max entities reached)
```

### Step 2: Create BattleChara
```
Call ClientObjectManager.CreateBattleCharacter(index, ...)
  → allocates from pre-allocated BattleChara memory pool
  → returns BattleChara* pointer
```

### Step 3: Configure Game Object Fields
```
Set on the BattleChara/GameObject struct:
  - Name: from BNpcName sheet (copy UTF-8 bytes to name buffer)
  - ObjectKind: BattleNpc (2)
  - SubKind: Combatant (5)
  - EntityId: generated unique ID (0xF0000000 + incrementing counter)
  - Position: requested position or player.Position + forward offset (3 yards)
  - Rotation: requested rotation or facing toward player
  - Scale: from BNpcBase.Scale
  - Height: from BNpcBase data
  - HitboxRadius: from BNpcBase data (typically 0.5 - 5.0)
  - TargetableStatus: set targetable flags
  - RenderFlags: set to visible
```

### Step 4: Load Model
```
Read BNpcBase → get ModelChara row ID
Configure DrawDataContainer with:
  - ModelCharaId
  - Equipment data (if applicable)
  - Customize data (race, body type, etc. for humanoid NPCs)
Initialize the DrawObject via the game's character setup pipeline
```

### Step 5: Register in GameObjectManager
```
Ensure the object appears in the game's sorted arrays so it can be:
  - Targeted by the player
  - Rendered by the game engine
  - Found via IObjectTable lookups
```

### Step 6: Initialize Simulation State
```
Create SimulatedEntityState:
  - MaxHp: calculated from BNpcBase base HP × level scaling × HpMultiplier
  - CurrentHp: MaxHp
  - Level: from request
  - Stats: derived from level and BNpcBase
  - Alive: true
```

## Despawning Process

```
1. Remove from our SpawnedNpcs tracking list
2. Disable rendering (DisableDraw)
3. Call ClientObjectManager.DeleteObjectByIndex(index)
4. Clean up SimulatedEntityState
5. Null out our BattleChara pointer
```

## NPC Type Data (from Game Sheets)

### BNpcName Sheet
- Provides: display name (string)
- Example: ID 5991 = "Striking Dummy"

### BNpcBase Sheet
- Provides: model type, scale, base stats, action lists
- Links to: ModelChara (visual), NpcEquip (equipment)
- Key fields:
  - `ModelChara` (row link) - which 3D model to use
  - `Scale` - visual scale factor
  - `Battalion` - AI behavior template

### ModelChara Sheet
- Provides: model file paths, skeleton type
- Key fields:
  - `Type` - model type (0=human, 1=demihuman, 2=monster, 3=weapon)
  - `Model` - model ID
  - `Base` - base ID
  - `Variant` - variant ID

## Preset NPC Types (Quick Spawn)

| Name | BNpcBase | Notes |
|------|----------|-------|
| Striking Dummy | (lookup) | Stationary, no attacks |
| Level 1 Ladybug | (lookup) | Simple, low HP |
| Level 50 Golem | (lookup) | Medium difficulty |
| Level 90 Boss | (lookup) | High HP, multiple skills |

IDs to be filled from actual game data during implementation.

## Position Calculation

Default spawn position (when Position is null):
```
playerPos = localPlayer.Position
playerRot = localPlayer.Rotation
forward = new Vector3(-sin(playerRot), 0, -cos(playerRot))
spawnPos = playerPos + forward * 5.0f  // 5 yards in front
npcRot = playerRot + π  // facing player
```

## Constraints
- Maximum 10 simultaneous NPCs (self-imposed limit for performance)
- Only use client-side indexes 200-448
- Never reuse an index that's already occupied
- Always clean up on plugin disposal
- All NPCs despawned if simulation is stopped
