# Spec: Network Safety Layer

## Overview
The network safety layer ensures that NO combat-related data is sent to the
FFXIV server during simulation. Additionally, the MovementBlockHook provides
client-side position control for the player (death freeze) and NPC targets
(approach positioning).

## Defense in Depth Strategy

```
Layer 1: Hyperborea's Packet Firewall (optional)
  └── Blocks ALL outgoing packets except heartbeat
  └── Blocks ALL incoming packets except whitelisted opcodes

Layer 2: UseAction Hook
  └── Intercepts UseAction() calls
  └── Blocks the call from reaching the game's network layer
  └── Routes to our simulation engine instead
  └── Works independently of Hyperborea

Layer 3: Client-Only Object Manipulation
  └── NPC selection modifies only client-side ObjectKind/SubKind
  └── Server has no knowledge of these changes
  └── No entity sync packets generated for ObjectKind modifications
```

## UseAction Hook

### Hook Target
```csharp
// FFXIVClientStructs: ActionManager.UseAction
private delegate bool UseActionDelegate(
    ActionManager* actionManager,
    ActionType actionType,
    uint actionId,
    ulong targetId,
    uint extraParam,
    ActionManager.UseActionMode mode,
    uint comboRouteId,
    bool* outOptAreaTargeted);

private Hook<UseActionDelegate> useActionHook;
```

### Hook Behavior
1. If simulation is not active → pass through to original
2. If actionType is not Action (1) → pass through (emotes, mounts work normally)
3. If target is not a selected combat NPC → pass through
4. Otherwise: **INTERCEPT** — route to CombatEngine, do NOT call original
5. Auto-register unregistered targets on first attack

### Key Safety Rules

1. **NEVER call `useActionHook.Original()` for simulated combat targets**
2. **Always pass through non-combat action types**
3. **Always pass through when simulation is inactive**
4. **Always pass through for non-simulated targets**

## MovementBlockHook

### Purpose
Hooks `GameObject.SetPosition` and `GameObject.SetRotation` at the native
function level via `IGameInteropProvider.HookFromAddress`. Provides two
independent blocking modes:

1. **Player death freeze**: When `IsBlocking = true`, blocks all position/rotation
   changes for the local player. Used during simulated death to prevent movement.

2. **NPC approach control**: Blocks server-driven position/rotation updates for
   specific NPCs whose positions are being controlled by the target approach
   feature. This prevents flickering from server updates fighting our writes.

### Implementation
```csharp
public unsafe class MovementBlockHook : IDisposable
{
    // Player death freeze
    public bool IsBlocking { get; set; }

    // NPC approach position blocking
    private readonly HashSet<nint> approachBlockedAddresses;
    public void AddApproachNpc(nint address);
    public void RemoveApproachNpc(nint address);
    public void ClearApproachNpcs();
}
```

### Hook Detours
```csharp
private void SetPositionDetour(GameObject* thisPtr, float x, float y, float z)
{
    // Block 1: Player death freeze
    if (IsBlocking && IsLocalPlayer(thisPtr))
        return;

    // Block 2: NPC approach control
    if (approachBlockedAddresses.Contains((nint)thisPtr))
        return;

    setPositionHook.Original(thisPtr, x, y, z);
}
// SetRotationDetour follows same pattern
```

### Signatures
- `GameObject.SetPosition`: via `GameObject.MemberFunctionPointers.SetPosition`
- `GameObject.SetRotation`: via `GameObject.MemberFunctionPointers.SetRotation`

### Notes
- Both hooks enabled immediately at plugin load; they gate on internal state
- Player death block is set by CombatEngine on simulated death, cleared on reset/stop
- NPC approach block is managed by NpcAiController, cleared on dispose/stop
- Direct memory writes (`gameObj->Position = value`) bypass SetPosition and are
  NOT intercepted by the hook — this is how we set controlled positions

## What We NEVER Do

| Action | Why It's Dangerous |
|--------|-------------------|
| Call UseAction.Original for sim targets | Sends action request packet |
| Write to real player HP/MP/Status | Server would detect mismatch |
| Modify real ActionManager cooldowns | Could desync client state |
| Send any IPC type 3 packets | Direct server communication |
| Modify targeting system for real entities | Server validates targets |
| Change player position programmatically | Server tracks position |

## What We Safely Do

| Action | Why It's Safe |
|--------|--------------|
| Read player stats from BattleChara | Read-only, no network |
| Change NPC ObjectKind to BattleNpc | Client-side struct field only |
| Call ActionEffectHandler.Receive with fake data | Client-side animation only |
| Write to NPC position via direct memory | Client-only visual change |
| Block NPC SetPosition via hook | Prevents server overwrite of our position |
| Render ImGui overlays | Pure UI layer |
| Read game data sheets via Lumina | Local file access |
