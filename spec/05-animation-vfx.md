# Spec: Animation & VFX Controller

## Overview
Triggers combat animations, visual effects, damage flytext, and death
animations by fabricating ActionEffect data and feeding it to the game's
native ActionEffectHandler. This makes combat look identical to real
FFXIV combat.

## Public Interface

```csharp
public class AnimationController : IDisposable
{
    /// Trigger attack animation + damage display for an action.
    void PlayActionEffect(ActionEffectRequest request);

    /// Trigger death animation on an entity.
    void PlayDeathAnimation(SimulatedNpc npc);

    /// Trigger hit reaction on the player.
    void PlayPlayerHitReaction();

    /// Trigger NPC auto-attack animation.
    void PlayNpcAutoAttack(SimulatedNpc npc, ulong targetId);
}
```

## ActionEffect Fabrication

### The Core Mechanism

The game processes combat results through `ActionEffectHandler.Receive()`.
By calling this function with fabricated data, we trigger the EXACT same
animation, VFX, and flytext pipeline that the game uses for real combat.

```csharp
// ActionEffectHandler.Receive signature:
public static partial void Receive(
    uint casterId,           // Entity ID of the action source
    Vector3* casterPosition, // World position of caster
    Header* header,          // Action metadata
    TargetEffects* effects,  // Per-target damage/heal/status data
    GameObjectId* targetIds  // Array of target entity IDs
);
```

### Header Structure

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
public struct ActionEffectHeader
{
    [FieldOffset(0x00)] public ulong AnimationTargetId;  // Who plays the animation
    [FieldOffset(0x08)] public uint ActionId;            // Which action was used
    [FieldOffset(0x0C)] public uint SpellId;             // Internal spell ID
    [FieldOffset(0x10)] public float AnimationLockDuration; // Lock after animation
    [FieldOffset(0x14)] public ushort RotationInt;       // Caster rotation (quantized)
    [FieldOffset(0x16)] public byte Flags;               // bit 0: ShowInLog
    [FieldOffset(0x17)] public byte TargetCount;         // Number of targets hit
    // Additional fields for sequence tracking
}
```

### Effect Structure (per target)

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0x08)]
public struct ActionEffect
{
    [FieldOffset(0x00)] public byte Type;     // Effect type
    [FieldOffset(0x01)] public byte Param1;   // Type-specific param
    [FieldOffset(0x02)] public byte Param2;
    [FieldOffset(0x03)] public byte Param3;
    [FieldOffset(0x04)] public byte Param4;
    [FieldOffset(0x05)] public byte Param5;
    [FieldOffset(0x06)] public ushort Value;  // Damage/heal amount
}
```

### Effect Types

| Type | Name | Description |
|------|------|-------------|
| 0 | Nothing | No effect |
| 1 | Miss | Action missed |
| 2 | FullResist | Fully resisted |
| 3 | Damage | Deal damage |
| 4 | Heal | Restore HP |
| 14 | StatusApply | Apply a status effect |
| 15 | StatusRemove | Remove a status effect |
| 27 | MpLoss | Drain MP |
| 28 | MpGain | Restore MP |

### Damage Effect Encoding

For a damage effect (Type = 3):
```
Param1: flags
  bit 0: critical hit
  bit 1: direct hit
  bit 2-3: damage type (0=physical, 1=magical, 2=unique)
Param2: bonus percent (for combo/positional)
Value: damage amount (0-65535)

For damage > 65535:
  Param4-5 encode the high bits
  Effective damage = Value | (Param4 << 16) | (Param5 << 24)
```

### Heal Effect Encoding

For a heal effect (Type = 4):
```
Param1: flags
  bit 0: critical heal
Value: heal amount
```

## ActionEffectRequest

```csharp
public class ActionEffectRequest
{
    public uint SourceEntityId { get; set; }
    public Vector3 SourcePosition { get; set; }
    public uint ActionId { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public float SourceRotation { get; set; }

    // Target effects
    public List<TargetEffect> Targets { get; set; } = new();
}

public class TargetEffect
{
    public ulong TargetId { get; set; }
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public DamageType DamageType { get; set; }
    public List<uint> AppliedStatuses { get; set; } = new();
}
```

## Building and Sending ActionEffect

```csharp
public unsafe void PlayActionEffect(ActionEffectRequest request)
{
    // 1. Allocate native memory for the structs
    var header = (ActionEffectHeader*)Marshal.AllocHGlobal(sizeof(ActionEffectHeader));
    var effects = (TargetEffects*)Marshal.AllocHGlobal(
        sizeof(TargetEffects) * request.Targets.Count);
    var targetIds = (GameObjectId*)Marshal.AllocHGlobal(
        sizeof(GameObjectId) * request.Targets.Count);

    try
    {
        // 2. Fill header
        *header = new ActionEffectHeader
        {
            AnimationTargetId = request.SourceEntityId,
            ActionId = request.ActionId,
            SpellId = request.ActionId, // Usually same as ActionId
            AnimationLockDuration = request.AnimationLock,
            RotationInt = QuantizeRotation(request.SourceRotation),
            Flags = 0x01, // ShowInLog
            TargetCount = (byte)request.Targets.Count,
        };

        // 3. Fill per-target effects
        for (int i = 0; i < request.Targets.Count; i++)
        {
            var target = request.Targets[i];
            targetIds[i] = new GameObjectId(target.TargetId);

            // Build effect entry
            var effect = new ActionEffect();
            if (target.Damage > 0)
            {
                effect.Type = 3; // Damage
                effect.Value = (ushort)(target.Damage & 0xFFFF);
                effect.Param1 = 0;
                if (target.IsCritical) effect.Param1 |= 0x01;
                if (target.IsDirectHit) effect.Param1 |= 0x02;
                // High bits for large damage
                if (target.Damage > 0xFFFF)
                {
                    effect.Param4 = (byte)((target.Damage >> 16) & 0xFF);
                    effect.Param5 = (byte)((target.Damage >> 24) & 0xFF);
                }
            }
            else if (target.Healing > 0)
            {
                effect.Type = 4; // Heal
                effect.Value = (ushort)(target.Healing & 0xFFFF);
                if (target.IsCritical) effect.Param1 |= 0x01;
            }

            effects[i].Effects[0] = effect;
            // Fill remaining effect slots with zeros (up to 8 per target)
        }

        // 4. Get caster position
        var pos = request.SourcePosition;

        // 5. Call the game's handler
        ActionEffectHandler.Receive(
            request.SourceEntityId,
            &pos,
            header,
            effects,
            targetIds);
    }
    finally
    {
        Marshal.FreeHGlobal((nint)header);
        Marshal.FreeHGlobal((nint)effects);
        Marshal.FreeHGlobal((nint)targetIds);
    }
}
```

## Death Animation

```csharp
public unsafe void PlayDeathAnimation(SimulatedNpc npc)
{
    if (npc.BattleChara == null) return;

    // Set character mode to Dead
    var character = (Character*)npc.BattleChara;
    character->Mode = CharacterModes.Dead;
    character->ModeParam = 0;

    // The game automatically plays the death animation
    // when Mode transitions to Dead
}
```

## Auto-Attack Animation

All NPC actions (auto-attacks AND skills) use **ActionId 7** (generic auto-attack).
Player-specific ActionIds (31, 141, 144, etc.) animate inconsistently on monster
models, so ActionId 7 is used universally for reliable animations on all NPCs.

```csharp
public void PlayNpcAutoAttack(SimulatedNpc npc, ulong targetId)
{
    PlayActionEffect(new ActionEffectRequest
    {
        SourceEntityId = npc.SimulatedEntityId,
        SourcePosition = npc.Position,
        ActionId = 7, // Auto-attack
        AnimationLock = 0.6f,
        SourceRotation = npc.Rotation,
        Targets = new List<TargetEffect>
        {
            new TargetEffect
            {
                TargetId = targetId,
                Damage = npc.AutoAttackDamage,
                DamageType = DamageType.Physical,
            }
        }
    });
}
```

## Rotation Quantization

The game stores rotation as a ushort (0-65535 mapping to 0-2PI):

```csharp
private ushort QuantizeRotation(float radians)
{
    // Normalize to [0, 2PI]
    float normalized = ((radians % (2f * MathF.PI)) + 2f * MathF.PI) % (2f * MathF.PI);
    return (ushort)(normalized / (2f * MathF.PI) * 65535f);
}
```

## Battle Stance (Weapon Draw)

Active combat target NPCs are set to a "drawn weapon" battle-ready visual state.
This is enforced every frame while the NPC is alive and in combat:

```csharp
public void SetBattleStance(SimulatedNpc npc)
{
    var character = (Character*)npc.BattleChara;
    character->CharacterData.InCombat = true;
    character->CharacterData.IsHostile = true;
    character->Timeline.IsWeaponDrawn = true;
    character->Timeline.ModelState = 1;
}
```

All four flags must be set together — `InCombat` alone is insufficient for most
monster models. `ClearBattleStance()` resets all flags when simulation stops.

## Fallback: Direct VFX Spawning

If ActionEffectHandler.Receive() proves unreliable, we can spawn VFX directly:

```csharp
// Hook the game's VFX spawn function
// Signature for ActorVfx.Create (to be found via sigscanning)
// Parameters: VFX path, source actor, target actor, ...

// This allows us to manually trigger specific VFX files
// e.g., "vfx/common/eff/hit_common_01.avfx" for a generic hit effect
```

This is the backup plan — ActionEffectHandler is strongly preferred.

## Thread Safety

- All animation calls MUST happen on the framework thread
- Use IFramework.RunOnFrameworkThread() if called from elsewhere
- Native memory allocations freed in the same call (no dangling pointers)

## Constraints

- ActionEffect data must be well-formed or the game will crash
- Sequence numbers should be monotonically increasing
- Target entity IDs must correspond to objects in the object table
- Maximum 8 effects per target, maximum 32 targets per action
