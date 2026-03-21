# FFXIV Dalamud Plugin Development - Combat Simulation Research

## Table of Contents
1. [Dalamud Plugin Development Basics](#1-dalamud-plugin-development-basics)
2. [FFXIV Client-Side Combat System](#2-ffxiv-client-side-combat-system)
3. [Spawning Battle NPCs](#3-spawning-battle-npcs-battlenpcbattlechara)
4. [Animation System](#4-animation-system)
5. [Existing Combat-Related Plugins](#5-existing-combat-related-plugins)
6. [FFXIVClientStructs Key Structs](#6-ffxivclientstructs-key-combat-structs)
7. [Safety Considerations](#7-safety-considerations)

---

## 1. Dalamud Plugin Development Basics

### Project Structure

```
MySolution/
├── MyPlugin/
│   ├── MyPlugin.csproj
│   ├── MyPlugin.json          # Plugin manifest
│   ├── packages.lock.json
│   └── Plugin.cs              # Entry point implementing IDalamudPlugin
└── MySolution.sln
```

### .csproj Configuration (Modern)

The modern approach uses the Dalamud.NET.Sdk which handles most boilerplate:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Dalamud.NET.Sdk/14.0.2">
  <PropertyGroup>
    <Version>0.0.0.1</Version>
    <PackageProjectUrl>https://github.com/YourUser/YourPlugin</PackageProjectUrl>
    <PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

The SDK automatically sets `TargetFramework` (currently net8.0), adds required Dalamud references, and configures the build pipeline. The older approach used `net472` with manual assembly references.

### Plugin Manifest (MyPlugin.json)

Minimum required fields (DalamudPackager auto-populates InternalName, AssemblyVersion, DalamudApiLevel):

```json
{
  "Name": "My Combat Simulator",
  "Author": "YourName",
  "Punchline": "Client-side combat simulation for practice.",
  "Description": "A detailed description of the plugin.",
  "RepoUrl": "https://github.com/YourUser/YourPlugin"
}
```

### IDalamudPlugin Interface

```csharp
public interface IDalamudPlugin : IDisposable
{
    // Plugin must implement Dispose() for cleanup
}
```

All plugins must implement `IDalamudPlugin`. The plugin DLL must contain exactly ONE class implementing this interface. Dalamud automatically instantiates it and injects services via the constructor.

### Plugin Entry Point (Modern Pattern)

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.IoC;

public class Plugin : IDalamudPlugin
{
    // Services are injected via constructor parameters
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        IGameGui gameGui,
        IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.framework = framework;

        // Register commands, hooks, UI, etc.
        this.pluginInterface.UiBuilder.Draw += DrawUI;
        this.pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
    }

    public void Dispose()
    {
        // MUST clean up all hooks, events, and resources
        this.pluginInterface.UiBuilder.Draw -= DrawUI;
        this.pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
    }
}
```

### Plugin Lifecycle

```
Game Process Launch
    -> Dalamud.Boot (Native) Injected
    -> .NET CoreCLR Loaded
    -> EntryPoint.Initialize Called
    -> ServiceManager.InitializeProvidedServices()
    -> ServiceManager.InitializeEarlyLoadableServices()
    -> Game Thread Resumes
    -> PluginManager.LoadPlugins()
        -> Assembly reflection finds IDalamudPlugin implementation
        -> DalamudPluginInterface instance created per plugin
        -> Plugin constructor invoked (receives injected services)
        -> ServiceScope created
        -> UiBuilder events subscribed
        -> Plugin Ready
    ...
    -> Plugin Unload:
        -> Dispose() called
        -> ServiceScope disposes scoped services
        -> Resources cleaned up in reverse dependency order
```

### Key Available Services

| Service | Purpose |
|---------|---------|
| `IClientState` | Player data, territory, login state |
| `ICommandManager` | Chat command registration |
| `IDataManager` | Game data file access (via Lumina) |
| `IGameGui` | Game UI element interaction |
| `IChatGui` | Chat message manipulation |
| `IObjectTable` | Access to spawned game objects |
| `IFramework` | Per-frame update hooks |
| `IGameInteropProvider` | Hook creation |
| `ITextureProvider` | Texture loading |
| `ICondition` | Game condition flags |

### How to Hook Game Functions

Dalamud uses signature scanning to find function addresses at runtime (resilient to game updates):

```csharp
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

public class MyPlugin : IDalamudPlugin
{
    // 1. Define a delegate matching the target function's signature
    private delegate bool UseActionDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    // 2. Create hook variable
    private Hook<UseActionDelegate>? useActionHook;

    public MyPlugin(IGameInteropProvider hookProvider)
    {
        // 3. Hook from FFXIVClientStructs member function pointer
        this.useActionHook = hookProvider.HookFromAddress<UseActionDelegate>(
            ActionManager.MemberFunctionPointers.UseAction,
            this.UseActionDetour);
        this.useActionHook.Enable();
    }

    // 4. Detour function - runs instead of original
    private bool UseActionDetour(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        // Custom logic here (before or after original)
        // ...

        // Call original function
        return this.useActionHook!.Original(
            actionManager, actionType, actionId, targetId,
            extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    public void Dispose()
    {
        this.useActionHook?.Dispose();
    }
}
```

**Alternative: Using SigScanner directly**

```csharp
// For functions not in FFXIVClientStructs:
var address = sigScanner.ScanText("40 53 55 57 41 56 41 57 48 83 EC 60");
var hook = hookProvider.HookFromAddress<MyDelegate>(address, MyDetour);
```

---

## 2. FFXIV Client-Side Combat System

### ActionManager (Singleton)

The `ActionManager` is the central hub for all action execution. Access via:

```csharp
var actionManager = ActionManager.Instance();
```

**Size:** 0x800 bytes

#### Key Fields

**Animation & Cooldown:**
- `0x08` - `float AnimationLock` - Current animation lock timer (counts toward zero)
- `0x0C-0x18` - Companion/Buddy/Pet action cooldowns

**Cast State:**
- `0x24` - `uint CastSpellId`
- `0x28` - `ActionType CastActionType`
- `0x2C` - `uint CastActionId`
- `0x30` - `float CastTimeElapsed`
- `0x34` - `float CastTimeTotal`
- `0x38` - `GameObjectId CastTargetId`
- `0x40` - `Vector3 CastTargetPosition`

**Action Queue:**
- `0x68` - `bool ActionQueued`
- `0x6C` - `ActionType QueuedActionType`
- `0x70` - `uint QueuedActionId`
- `0x78` - `GameObjectId QueuedTargetId`
- `0x84` - `UseActionMode QueueType`

**Combo System:**
- `0x60` - `ComboDetail Combo` (Timer + Action fields)

**Cooldown Tracking:**
- `0x184` - `FixedSizeArray80<RecastDetail> _cooldowns`

**Tracking:**
- `0x120` - `ushort LastUsedActionSequence` (monotonically increasing)
- `0x122` - `ushort LastHandledActionSequence`

#### Key Methods

```csharp
// Execute an action
bool UseAction(ActionType actionType, uint actionId,
    ulong targetId = 0xE000_0000, uint extraParam = 0,
    UseActionMode mode = UseActionMode.None, uint comboRouteId = 0,
    bool* outOptAreaTargeted = null);

// Execute ground-targeted action
bool UseActionLocation(ActionType actionType, uint actionId,
    ulong targetId, Vector3* location, uint extraParam, byte a7);

// Check if action can be used (returns 0 if OK, error code otherwise)
uint GetActionStatus(ActionType actionType, uint actionId,
    ulong targetId = 0xE000_0000, bool checkRecastActive = true,
    bool checkCastingActive = true, uint* outOptExtraInfo = null);

// Get the upgraded/transformed action ID (e.g., combo continuations)
uint GetAdjustedActionId(uint actionId);

// Cooldown queries
float GetRecastTime(ActionType actionType, uint actionId);
float GetRecastTimeElapsed(ActionType actionType, uint actionId);
bool IsRecastTimerActive(ActionType actionType, uint actionId);
bool IsActionOffCooldown(ActionType actionType, uint actionId);
RecastDetail* GetRecastGroupDetail(int recastGroup);

// Range checks
bool IsActionTargetInRange(ActionType actionType, uint actionId);
static float GetActionRange(uint actionId);
static uint GetActionInRangeOrLoS(uint actionId, GameObject* source, GameObject* target);

// Resource checks
uint CheckActionResources(ActionType actionType, uint actionId, void* actionData = null);

// Cast time (with proc adjustments)
static int GetAdjustedCastTime(ActionType actionType, uint actionId,
    bool applyProcs = true, CastTimeProc* outOptProc = null);

// Targeting
static bool CanUseActionOnTarget(uint actionId, GameObject* target);
static TargetCategory ClassifyTarget(Character* target);

// Cast bar display
void OpenCastBar(BattleChara* character, ActionType actionType,
    uint actionId, uint spellId, uint extraParam,
    float castTimeElapsed, float castTimeTotal);
```

#### Action Types (ActionType enum)

```csharp
public enum ActionType : uint {
    None = 0,
    Action = 1,      // Regular combat actions
    Item = 2,
    EventItem = 3,
    EventAction = 4,
    GeneralAction = 5,
    BuddyAction = 6,
    MainCommand = 7,
    Companion = 8,
    CraftAction = 9,
    PetAction = 11,
    Mount = 13,
    PvPAction = 14,
    FieldMarker = 15,
    Ornament = 20,
}
```

### How Actions Execute (Client-Side Flow)

1. **Player presses action button** -> Client calls `ActionManager.UseAction()`
2. **Client-side validation:**
   - Checks action unlock status (class/level)
   - Validates target (range, line of sight, target category)
   - Checks cooldown/recast status
   - Checks resource availability (MP, gauge, charges)
3. **If action is queueable and conditions not met:** Stores in queue fields
4. **Client applies initial animation lock** (0.5s for oGCDs, 0.1s during casts)
5. **Client sends action request packet to server** (IPC type 3 segment)
6. **Server validates and responds with ActionEffect packet:**
   - Overwrites client animation lock (typically 0.6s)
   - Contains damage/heal values, status effects, etc.
7. **Client processes ActionEffect via ActionEffectHandler.Receive()**
8. **Animations, VFX, and flytext triggered client-side**

### Action Queue System

The queue operates within a window before the GCD/recast ends:

- Actions pressed during animation lock or before recast completes are queued
- `ActionQueued` flag set to true, action details stored in `QueuedActionId`, `QueuedTargetId`, etc.
- On each frame update (`ActionManager.Update()`), queued action is attempted
- Queue supports modes: `None`, `Queue`, `Macro`, `Combo`

### Animation Lock Details

```
Standard oGCD:     Client sets 0.5s -> Server overwrites to ~0.6s
Standard GCD:      Client sets 0.1s during cast -> Server sets lock after cast
Limit Break:       Up to 2.0s
Food/Items:        Varies (0.8s+)
```

**Critical:** The server response overwrites the client timer, meaning effective lock = round_trip_latency + server_lock_duration. The game does NOT compensate for ping.

### RecastDetail Structure

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0x14)]
public struct RecastDetail {
    [FieldOffset(0x0)] public bool IsActive;
    [FieldOffset(0x4)] public uint ActionId;
    [FieldOffset(0x8)] public float Elapsed;
    [FieldOffset(0xC)] public float Total;
}
```

### ComboDetail Structure

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0x8)]
public struct ComboDetail {
    [FieldOffset(0x00)] public float Timer;    // Combo window remaining
    [FieldOffset(0x04)] public uint Action;    // Current combo action
}
```

---

## 3. Spawning Battle NPCs (BattleNpc/BattleChara)

### Object Hierarchy

```
GameObject (0x1A0 bytes)
  └── Character (0x2370 bytes, inherits GameObject + CharacterData)
        └── BattleChara (0x3810 bytes, inherits Character)
              ├── StatusManager  @ 0x23B0
              ├── CastInfo       @ 0x2790
              ├── ActionEffectHandler @ 0x2900
              └── ForayInfo      @ 0x3800
```

### GameObject Key Fields

```
Offset  Type                    Field               Notes
------  ----                    -----               -----
0x30    byte[64]                Name                UTF-8 name buffer
0x78    uint                    EntityId            Unique entity ID
0x8C    ushort                  ObjectIndex         Index in object table
0x90    ObjectKind              ObjectKind          Type enum (byte)
0x91    byte/BattleNpcSubKind   SubKind             Subtype
0x92    byte                    Sex
0x95    byte                    TargetStatus        Selection state
0x9A    ObjectTargetableFlags   TargetableStatus    Targetability flags
0xB0    Vector3                 Position            World position
0xC0    float                   Rotation            Facing direction
0xC4    float                   Scale
0xC8    float                   Height
0xD0    float                   HitboxRadius        Collision radius
0x100   DrawObject*             DrawObject          Visual representation
0x118   VisibilityFlags         RenderFlags         What renders
```

### ObjectKind Enum

```csharp
public enum ObjectKind : byte {
    None = 0,
    Pc = 1,           // Player character
    BattleNpc = 2,    // Combat NPC (enemies, allies)
    EventNpc = 3,     // Non-combat NPC
    Treasure = 4,
    Aetheryte = 5,
    GatheringPoint = 6,
    EventObj = 7,
    Mount = 8,
    Companion = 9,
    Retainer = 10,
    AreaObject = 11,
    HousingEventObject = 12,
    Cutscene = 13,
    Ornament = 15,
    CardStand = 16,
}
```

### BattleNpcSubKind Enum

```csharp
public enum BattleNpcSubKind : byte {
    BNpcPart = 1,        // Multi-part boss hitbox
    Pet = 2,             // Player pet
    Buddy = 3,           // Chocobo companion
    Combatant = 5,       // Regular enemy combatant
    RaceChocobo = 6,     // Chocobo racing
    LovmMinion = 7,      // Lord of Verminion
    NpcPartyMember = 9,  // Trust/NPC party members
}
```

**To make a BattleNpc appear as a hostile enemy:**
- `ObjectKind = ObjectKind.BattleNpc` (0x90 = 2)
- `SubKind = BattleNpcSubKind.Combatant` (0x91 = 5)
- `TargetableStatus` must have appropriate flags set (0x9A)

### Character Key Fields (Additional to GameObject)

```
Offset  Type                Field               Notes
------  ----                -----               -----
0x630   EmoteController     EmoteController     Emote management
0x6F8   DrawDataContainer   DrawData            Appearance/equipment
0xA30   TimelineContainer   Timeline            Animation timelines
0xD80   LookAtContainer     LookAt              Head tracking
0x1988  VfxContainer        Vfx                 Visual effects
0x1A90  EffectContainer     Effects             Active effects
0x1CE2  byte                RelationFlags       Party/Alliance/Friend bits
0x1CE8  byte                ActorControlFlags   0x40 = animation locked
0x2300  byte[7]             FreeCompanyTag
0x2308  GameObjectId        TargetId            Current target
0x231C  float               CastRotation
0x2338  uint                NameId              Name sheet row ID
0x2364  CharacterModes      Mode                Current mode
0x2365  byte                ModeParam
```

### CharacterModes Enum

```csharp
public enum CharacterModes : byte {
    None = 0,
    Normal = 1,
    Dead = 2,
    EmoteLoop = 3,
    Mounted = 4,
    Crafting = 5,
    Gathering = 6,
    AnimLock = 8,
    Carrying = 9,
    RidingPillion = 10,
    Performance = 16,
}
```

### ClientObjectManager - Creating Battle Characters

The `ClientObjectManager` manages a pool of `BattleChara` objects. This is the low-level mechanism used by plugins like Brio to spawn actors.

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0xFA0)]
public unsafe partial struct ClientObjectManager {
    // Singleton access
    public static partial ClientObjectManager* Instance();

    // Pre-allocated memory pool for BattleChara objects
    [FieldOffset(0x00)] public BattleChara* BattleCharaMemory;
    [FieldOffset(0x08)] public uint BattleCharaSize;

    // Array of 249 BattleChara entries
    [FieldOffset(0x10)] internal FixedSizeArray249<BattleCharaEntry> _battleCharas;

    // Core methods
    public partial BattleChara* CreateBattleCharacter(...);
    public partial GameObject* GetObjectByIndex(int index);
    public partial int GetIndexByObject(GameObject* obj);
    public partial void DeleteObjectByIndex(int index, ...);
    public partial int CalculateNextAvailableIndex(...);
}

// Each entry tracks allocated BattleChara
[StructLayout(LayoutKind.Explicit, Size = 0x10)]
public struct BattleCharaEntry {
    [FieldOffset(0x00)] public BattleChara* BattleChara;
    [FieldOffset(0x08)] public ObjectKind ObjectKind;
    [FieldOffset(0x0C)] public byte Index;
    [FieldOffset(0x0E)] public ushort MemoryIndex;
}
```

### GameObjectManager

Manages the global sorted arrays of all game objects:

```csharp
public unsafe partial struct GameObjectManager {
    public static partial GameObjectManager* Instance();

    // Three sorted arrays of 819 entries each:
    // 1. _indexSorted    - by ObjectIndex (null for inactive)
    // 2. _gameObjectIdSorted - active objects by GameObjectId
    // 3. _entityIdSorted     - active objects by EntityId

    public partial GameObject* GetObjectByGameObjectId(GameObjectId objectId);
    public partial GameObject* GetObjectByEntityId(uint entityId);
}
```

### IObjectTable (Dalamud Service)

The managed wrapper for accessing game objects:

```csharp
// Index ranges:
// [0, 199]     - CharacterManagerObjects (players, mounts, minions)
// [200, 448]   - ClientObjects (client-spawned actors)
// [449, 488]   - EventObjects
// [489, 628]   - StandObjects
// [629, 728]   - ReactionEventObjects

// Useful properties:
IPlayerCharacter? LocalPlayer;
IEnumerable<IBattleChara> PlayerObjects;
IEnumerable<IGameObject> ClientObjects;  // Client-spawned actors live here

// Methods:
IGameObject? SearchById(ulong objectId);
IGameObject? SearchByEntityId(uint entityId);
nint GetObjectAddress(int index);
IGameObject? CreateObjectReference(nint address);
```

### How Brio Spawns Actors

Brio spawns actors into the ObjectTable during GPose. The general approach:

1. **Find available index** in ClientObjectManager (indexes 200-448 are client-controlled)
2. **Call `CreateBattleCharacter()`** on the ClientObjectManager to allocate from the BattleChara pool
3. **Configure the created object:**
   - Set `ObjectKind` (e.g., `Pc` for player-like, `BattleNpc` for NPC)
   - Set `SubKind` as appropriate
   - Set name, appearance data via DrawDataContainer
   - Set position, rotation, scale
4. **Register in GameObjectManager** so it appears in sorted arrays
5. **Configure rendering** via DrawObject and RenderFlags

**Key constraint:** Brio currently works within GPose. Spawning actors outside GPose requires hooking into the character creation pipeline more deeply. Up to 239 actors can be spawned.

### What Makes a BattleNpc Appear as a Targetable Enemy

To simulate a targetable enemy NPC, these fields must be set:

1. `ObjectKind = BattleNpc` (2)
2. `SubKind = Combatant` (5)
3. `TargetableStatus` - Set appropriate flags for player targeting
4. `Name` - Write name bytes to the 64-byte buffer at 0x30
5. `EntityId` - Assign a unique entity ID (use a high/unused range)
6. `Position` - World position vector
7. `Rotation` - Facing direction
8. `HitboxRadius` - Collision/targeting radius
9. `Scale` / `Height` - Visual scaling
10. `DrawObject` - Must have a valid model loaded
11. `RenderFlags` - Must be set to visible

For an HP bar to display, the BattleChara-level fields in the StatusManager and Character data must also be configured (HP values are typically managed server-side in normal gameplay).

---

## 4. Animation System

### Animation Architecture

FFXIV's animation system uses three key file types:

| File Type | Extension | Purpose |
|-----------|-----------|---------|
| Timeline  | .tmb      | Triggers animations, VFX, and sounds. Referenced when activating abilities |
| Animation | .pap      | Actual bone movements (skeleton animation data) |
| VFX       | .avfx     | Particle effects and glow effects. No impact on character motion |

**Flow:** Action used -> TMB timeline file loaded -> TMB triggers .pap animations + .avfx VFX + .scd sounds

### VfxContainer (on Character)

Located at offset `0x1988` on Character struct:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0xF0)]
public unsafe partial struct VfxContainer {
    // 14 VfxData pointers (index [6] = Omen effects)
    [FieldOffset(0x18)] internal FixedSizeArray14<Pointer<VfxData>> _vfxData;

    // 2 tether connections
    [FieldOffset(0xA0)] internal FixedSizeArray2<Tether> _tethers;

    [FieldOffset(0xD0)] public ushort VoiceId;
}

// Tether sub-structure
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct Tether {
    [FieldOffset(0x00)] public ushort Id;          // Channeling sheet row ID
    [FieldOffset(0x02)] public byte Progress;       // 0-100
    [FieldOffset(0x10)] public GameObjectId TargetId;
}
```

### VFX Types

Two primary VFX spawn mechanisms exist in the game client:

1. **ActorVfx** - VFX attached to a specific actor/character (follows them)
   - Spawned via game functions typically hooked with signatures
   - Takes parameters: VFX path (e.g., "vfx/common/eff/..."), source actor, target actor

2. **StaticVfx** - VFX spawned at a fixed world position
   - Takes parameters: VFX path, position coordinates

Both types reference `.avfx` files from the game's data archives.

### Spawning VFX Client-Side

Plugins like VFXEditor and RaidsRewritten interact with VFX by:

1. **Hooking VFX creation functions** to intercept and replace VFX paths
2. **Calling game VFX functions directly** to spawn arbitrary effects at positions/on actors
3. **Modifying TMB timelines** to change when/which VFX trigger

### TimelineContainer (on Character)

Located at offset `0xA30` on Character. Contains animation timeline data including:
- `IsWeaponDrawn` state
- Current animation playback state
- Timeline file references

### Playing Combat Animations

Combat animations are triggered through the TMB/timeline system:

1. **Normal flow:** ActionManager.UseAction -> server processes -> ActionEffect received -> timeline triggered -> animation plays
2. **Client-side simulation:** Can trigger animations by:
   - Hooking the timeline/animation playback functions
   - Directly calling animation functions with specific animation IDs
   - Using the emote/animation system (EmoteController at offset 0x630)

### ActionEffectHandler - Processing Combat Results

When the server sends an ActionEffect packet, the client processes it through `ActionEffectHandler.Receive()`:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0xF00)]
public unsafe partial struct ActionEffectHandler {
    // 32 incoming effect entries
    [FieldOffset(0x00)] internal FixedSizeArray32<EffectEntry> _incomingEffects;

    // Static entry point for ActionEffectN packets
    public static partial void Receive(
        uint casterId,
        Vector3* casterPosition,
        Header* header,
        TargetEffects* targetEffects,
        GameObjectId* targetIds);
}

// Header for an action effect (0x28 bytes)
public struct Header {
    public GameObjectId AnimationTargetId;
    public uint ActionId;
    public uint SpellId;
    public float AnimationLockDuration;
    public ushort RotationInt;  // Quantized rotation
    public byte Flags;          // bit 0: ShowInLog, bit 1: ForceAnimationLock
    // ... target count, etc.
}

// Per-target effect data (0x78 bytes)
public struct EffectEntry {
    public uint GlobalSequence;
    public byte TargetIndex;
    public byte ActionType;
    public uint ActionId;
    public ushort SpellId;
    public GameObjectId Source;
    public GameObjectId Target;
    public bool SourceConfirmed;
    public bool TargetConfirmed;
    public TargetEffects Effects;
}

// Individual effect (8 bytes) - damage, heal, status, etc.
public struct Effect {
    public byte Type;       // Effect type (damage, heal, status apply, etc.)
    public byte Param1;
    public byte Param2;
    public byte Param3;
    public byte Param4;
    public byte Param5;
    public ushort Value;    // Damage/heal amount
}

// Up to 8 effects per target
public struct TargetEffects {
    public FixedSizeArray8<Effect> Effects;  // 0x40 bytes
}
```

### Hooking ActionEffect for Client-Side Combat

To intercept and/or fabricate combat results:

```csharp
// Hook the Receive function
private Hook<ActionEffectHandler.Delegates.Receive> receiveHook;

// In the detour, you can:
// 1. Read incoming damage/heal values
// 2. Modify effect data before it reaches the client
// 3. Fabricate entirely fake ActionEffect data for client-side display
```

---

## 5. Existing Combat-Related Plugins

### RaidsRewritten (Key Reference)
- **Repo:** https://github.com/Ricimon/FFXIV-RaidsRewritten
- **Purpose:** Adds custom mechanics to existing fights client-side
- **Architecture:**
  - Hooks fight events to spawn fake attack VFX objects
  - Runs custom hit-detection checks (collision analysis)
  - Overrides player controls for stuns, knockbacks, etc.
  - Syncs RNG between players via fixed seed
  - **100% client-side** - nothing sent to/from server is modified
- **Dependencies:** Borrows from Splatoon (fight hooks), VFXEditor (VFX spawning), BossMod (action overrides), vnavmesh (movement overrides), Brio (model spawning), Penumbra (model replacements)
- **Player control overrides** only use actions players could normally perform (e.g., knockbacks are forced directional movement)

### BossMod (ffxiv_bossmod)
- **Repo:** https://github.com/awgil/ffxiv_bossmod
- **Has action queue system** with priority levels (Very Low 1000 -> Very High 5000)
- **ActionDefinition** record contains: AllowedClasses, MinLevel, CastTime, Cooldown, InstantAnimLock, CastAnimLock, MaxCharges
- **Manages action execution** through validation pipeline: unlock check -> target validation -> cooldown check -> resource check -> execute through game API

### VFXEditor
- **Repo:** https://github.com/0ceal0t/Dalamud-VFXEditor
- **Edits:** .avfx (VFX), .tmb (timelines), .pap (animations), .eid (bind points), .scd (sounds)
- **Works by:** Replacing VFX file references at runtime via hooks
- **Key insight:** VFX and animations are separate systems; VFX = particles/glow, animations = bone movements

### Brio
- **Repo:** https://github.com/Etheirys/Brio
- **Spawns up to 239 actors** in GPose
- **Uses:** ClientObjectManager to allocate BattleChara objects
- **Customizes:** Appearance via DrawDataContainer, animations via TimelineContainer
- **Dependencies:** FFXIVClientStructs, Penumbra, Glamourer

### FFXIVActionEffectRange
- **Repo:** https://github.com/yomishino/FFXIVActionEffectRange
- **Shows:** Effect range visualization for AoE actions
- **Hooks:** Action effect data to determine where actions landed and their coverage area

### MultiHit
- **Repo:** https://github.com/Bluefissure/MultiHit
- **Splits flytext** to match animation swings
- **Uses TMB-defined delays** for synchronized display

### IINACT
- **Parses combat data** as an ACT replacement
- **Reads ActionEffect data** for DPS/HPS tracking

---

## 6. FFXIVClientStructs Key Combat Structs

### Complete Object Hierarchy

```
GameObject                          (0x1A0)  - Base object
├── Character                       (0x2370) - Inherits GameObject + CharacterData
│   └── BattleChara                 (0x3810) - Combat-capable character
│       ├── StatusManager           @ 0x23B0 - Status effects
│       ├── CastInfo               @ 0x2790 - Cast state
│       ├── ActionEffectHandler    @ 0x2900 - Incoming effects
│       └── ForayInfo              @ 0x3800 - Eureka/Bozja info
├── Companion                       - Minion
├── Ornament                        - Fashion accessory
├── EventObject                     - Map objects
├── Aetheryte                       - Teleport crystals
├── Treasure                        - Loot chests
├── AreaObject                      - Area markers
├── GatheringPointObject            - Gathering nodes
├── HousingObject                   - Housing items
└── ReactionEventObject             - Reaction markers
```

### CastInfo (0x170 bytes)

```csharp
public partial struct CastInfo {
    [FieldOffset(0x00)] public bool IsCasting;
    [FieldOffset(0x01)] public bool Interruptible;
    [FieldOffset(0x02)] public byte ActionType;
    [FieldOffset(0x04)] public uint ActionId;
    [FieldOffset(0x08)] public uint SourceSequence;    // Monotonically increasing
    [FieldOffset(0x10)] public GameObjectId TargetId;
    [FieldOffset(0x20)] public Vector3 TargetLocation;
    [FieldOffset(0x30)] public float Rotation;
    [FieldOffset(0x34)] public float CurrentCastTime;
    [FieldOffset(0x38)] public float BaseCastTime;
    [FieldOffset(0x3C)] public float TotalCastTime;

    // Response fields (set when ActionEffect received from server)
    [FieldOffset(0x40)] public uint ResponseSpellId;
    [FieldOffset(0x44)] public ActionType ResponseActionType;
    [FieldOffset(0x48)] public uint ResponseActionId;
    [FieldOffset(0x4C)] public uint ResponseGlobalSequence;
    [FieldOffset(0x50)] public uint ResponseSourceSequence;
    [FieldOffset(0x58)] internal FixedSizeArray32<GameObjectId> _responseTargetIds;
    [FieldOffset(0x158)] public byte ResponseTargetCount;
    [FieldOffset(0x159)] public byte ResponseFlags;
}
```

### StatusManager

Contains active status effects (buffs/debuffs) on a character. Accessed via:
- `BattleChara` at offset `0x23B0`
- `Character.GetStatusManager()` virtual function (slot 77)

Key methods:
- `HasStatus(uint statusId)` - Check if status is active
- `SetStatus(int statusIndex, ushort statusId, float remaining, ushort param, uint sourceId, byte refreshFlags)` - Apply/modify a status
- `Status[index]` - Direct access to status array

The Dalamud wrapper `StatusList` provides managed access:
```csharp
var statusList = battleChara.StatusList;
foreach (var status in statusList)
{
    var id = status.StatusId;        // Status effect ID
    var remaining = status.RemainingTime;  // Time left
    var sourceId = status.SourceId;  // Who applied it
    var stacks = status.StackCount;  // Stack count
    var param = status.Param;        // Extra parameter
}
```

### ActionManager (Complete Reference)

See Section 2 above for the full 0x800-byte struct with all fields and methods.

### Accessing from a Dalamud Plugin

```csharp
// Get the ActionManager singleton
var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();

// Check if an action is off cooldown
bool canUse = actionManager->IsActionOffCooldown(ActionType.Action, actionId);

// Get cooldown info
var recastGroup = actionManager->GetRecastGroup(1, actionId);
var detail = actionManager->GetRecastGroupDetail(recastGroup);
if (detail != null && detail->IsActive)
{
    float remaining = detail->Total - detail->Elapsed;
}

// Get adjusted action ID (handles combos, upgrades)
uint adjustedId = actionManager->GetAdjustedActionId(actionId);

// Check action status
uint status = actionManager->GetActionStatus(ActionType.Action, actionId, targetId);
if (status == 0) { /* Action can be used */ }

// Read current cast
float castProgress = actionManager->CastTimeElapsed / actionManager->CastTimeTotal;

// Read animation lock
float lockRemaining = actionManager->AnimationLock;

// Check queued action
if (actionManager->ActionQueued)
{
    uint queuedAction = actionManager->QueuedActionId;
}

// Access via Dalamud's IObjectTable
var localPlayer = objectTable.LocalPlayer;
if (localPlayer != null)
{
    // Access native struct
    unsafe
    {
        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)
            localPlayer.Address;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)
            localPlayer.Address;
        var battleChara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)
            localPlayer.Address;

        // Read cast info
        var castInfo = &battleChara->CastInfo;
        if (castInfo->IsCasting)
        {
            // Player is casting actionId with castTime remaining
        }

        // Read status effects
        var statusMgr = &battleChara->StatusManager;
        // Iterate statuses...
    }
}
```

---

## 7. Safety Considerations

### Client-Side Only Operations (SAFE)

These operations do NOT trigger server communication:

1. **Reading game memory** - All read operations on game structs
2. **Hooking functions** - Intercepting calls is purely client-side
3. **Modifying visual rendering** - DrawObject, RenderFlags, VFX replacement
4. **Spawning client-only actors** - Using ClientObjectManager (indexes 200-448)
5. **Playing animations/VFX** - Triggering .tmb/.pap/.avfx playback
6. **Modifying UI elements** - ImGui overlays, addon modifications
7. **Reading data files** - Lumina/DataManager access to game sheets

### Operations That TRIGGER Server Communication (DANGEROUS)

1. **`ActionManager.UseAction()`** - Sends action request packet to server
2. **Any IPC segment type 3 packets** - Chat messages, action requests, market board, etc.
3. **Position updates** - Client periodically sends position to server
4. **Targeting changes** (sometimes) - Server may validate targets
5. **Party/duty actions** - All duty-related operations
6. **Item usage** - Server validates inventory state

### How to Ensure Client-Side Only

1. **Never call `UseAction()` on the real ActionManager** for simulated combat - only read from it
2. **Hook `UseAction()` to BLOCK** any accidental outgoing action requests from simulated combat
3. **Use separate "fake" ActionManager state** for your simulation
4. **Spawned actors should use client-side indexes (200+)** so the server never sees them
5. **Do not write to network-synced fields** on the local player or real game objects
6. **VFX and animations are inherently client-side** - safe to trigger freely
7. **Fabricate ActionEffect data locally** rather than relying on server responses

### Dalamud Plugin Publishing Restrictions

For reference (these apply to official Dalamud plugin repository submissions):

- **Prohibited:** Automatic polling/requests without user interaction
- **Prohibited:** Actions "outside of specification" (things not possible normally)
- **Prohibited:** DPS meters, parsing, raid logging
- **Prohibited:** Visible AoE markers for non-telegraphed mechanics
- **Prohibited:** Any PvP advantage
- **Prohibited:** Emote looping, crafting automation
- **Allowed:** Displaying party/alliance info in different formats
- **Required:** Contact approval team for any combat-related plugin
- **Core principle:** "Enhance the experience, not radically alter it"

Note: These restrictions apply to official repository distribution. Third-party/custom repositories have their own policies, and local development has no restrictions.

### Recommended Architecture for Client-Side Combat Simulation

```
┌─────────────────────────────────────────────────────┐
│                Combat Simulator Plugin                │
├─────────────────────────────────────────────────────┤
│                                                       │
│  ┌──────────────┐   ┌──────────────────────────┐    │
│  │ Fake Entity  │   │  Simulated ActionManager  │    │
│  │  Manager     │   │  (No real UseAction calls)│    │
│  │              │   │  - Fake cooldowns          │    │
│  │  - Spawn via │   │  - Fake cast bars          │    │
│  │  ClientObj   │   │  - Fake animation locks    │    │
│  │  Manager     │   │  - Fake combo state        │    │
│  │  (idx 200+)  │   │  - Damage calculation      │    │
│  └──────┬───────┘   └──────────┬───────────────┘    │
│         │                      │                      │
│  ┌──────┴───────┐   ┌──────────┴───────────────┐    │
│  │ Fake Entity  │   │  Animation/VFX Controller │    │
│  │  State       │   │                            │    │
│  │  - HP        │   │  - Trigger .tmb timelines  │    │
│  │  - Statuses  │   │  - Spawn .avfx effects    │    │
│  │  - Position  │   │  - Play hit reactions       │    │
│  │  - Target    │   │  - Show damage flytext      │    │
│  └──────────────┘   └────────────────────────────┘    │
│                                                       │
│  ┌────────────────────────────────────────────────┐  │
│  │  UseAction Hook (BLOCKING)                      │  │
│  │  - Intercepts real UseAction calls              │  │
│  │  - Redirects to simulation instead of server    │  │
│  │  - Prevents any network traffic                 │  │
│  └────────────────────────────────────────────────┘  │
│                                                       │
│  ┌────────────────────────────────────────────────┐  │
│  │  ActionEffect Fabricator                        │  │
│  │  - Creates fake ActionEffect data               │  │
│  │  - Feeds to ActionEffectHandler.Receive()       │  │
│  │  - Triggers real client-side animations/VFX     │  │
│  └────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

---

## Sources

### Official Documentation
- Dalamud Plugin Development: https://dalamud.dev/plugin-development/project-layout/
- IDalamudPlugin Interface: https://dalamud.dev/api/Dalamud.Plugin/Interfaces/IDalamudPlugin/
- Plugin Restrictions: https://dalamud.dev/plugin-publishing/restrictions/
- Technical Considerations: https://dalamud.dev/plugin-development/technical-considerations/
- Reverse Engineering Guide: https://dalamud.dev/plugin-development/reverse-engineering/
- IObjectTable API: https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IObjectTable/

### Repositories
- FFXIVClientStructs: https://github.com/aers/FFXIVClientStructs
- Dalamud: https://github.com/goatcorp/Dalamud
- SamplePlugin: https://github.com/goatcorp/SamplePlugin
- RaidsRewritten: https://github.com/Ricimon/FFXIV-RaidsRewritten
- BossMod: https://github.com/awgil/ffxiv_bossmod
- Brio: https://github.com/Etheirys/Brio
- VFXEditor: https://github.com/0ceal0t/Dalamud-VFXEditor
- ReAction: https://github.com/UnknownX7/ReAction
- ActionEffectRange: https://github.com/yomishino/FFXIVActionEffectRange
- MultiHit: https://github.com/Bluefissure/MultiHit

### Technical References
- FFXIVClientStructs Docs: https://ffxiv.wildwolf.dev/
- XIV Dev Wiki (Packets): https://xiv.dev/network/packet-structure
- XIV Dev Wiki (Animation Lock): https://xiv.dev/game-internals/actions/animation-lock
- BossMod Action Queue (DeepWiki): https://deepwiki.com/awgil/ffxiv_bossmod/4.1-action-queue-system
- Dalamud Game Integration (DeepWiki): https://deepwiki.com/goatcorp/Dalamud/5-game-integration
- Dalamud Plugin Interface (DeepWiki): https://deepwiki.com/goatcorp/Dalamud/3.2-plugin-interface

### Key Source Files (FFXIVClientStructs)
- `FFXIV/Client/Game/ActionManager.cs` - Action execution system
- `FFXIV/Client/Game/Object/GameObject.cs` - Base game object
- `FFXIV/Client/Game/Object/ClientObjectManager.cs` - Client-side object creation
- `FFXIV/Client/Game/Object/GameObjectManager.cs` - Global object management
- `FFXIV/Client/Game/Character/Character.cs` - Character struct
- `FFXIV/Client/Game/Character/BattleChara.cs` - Combat character
- `FFXIV/Client/Game/Character/CastInfo.cs` - Casting state
- `FFXIV/Client/Game/Character/ActionEffectHandler.cs` - Combat effect processing
- `FFXIV/Client/Game/Character/StatusManager.cs` - Status effects
- `FFXIV/Client/Game/Character/VfxContainer.cs` - Visual effects
- `FFXIV/Client/Game/Character/TimelineContainer.cs` - Animations
