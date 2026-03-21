# Known Issues

## Movement blocking on player death
**Status: FIX IMPLEMENTED** — Hooks `InputData.IsInputIdDown/Pressed/Held` to return false for movement InputIds when player is dead. See `Safety/MovementBlockHook.cs`.

### Current approach (position hook)
Hooks `GameObject.SetPosition` and `GameObject.SetRotation` at the native function level via `IGameInteropProvider.HookFromAddress`. When `IsBlocking` is true and the target object is the local player, the hooks skip the original call — position and rotation simply never change. Unlike input-based approaches, this prevents movement at the destination (the game engine calculates movement normally but the position write is silently dropped), so there is no shaking, no input side-effects, and no timing issues.

### Previous approaches tried (all failed)
0. **InputId query hooks** — Hooked `InputData.IsInputIdDown/Pressed/Held` to return false for movement InputIds. Result: character jumps erratically and strange keyboard behavior. The hook was too broad — `IsInputIdDown` is used for all input queries, not just movement, causing widespread side-effects.
1. **Position/rotation forcing per tick** — Saved death position, forced `GameObject->Position` and `Rotation` back every frame. Result: character shakes violently due to game engine fighting the correction each frame.
2. **CharacterModes.EmoteLoop** — Set `Character->Mode = CharacterModes.EmoteLoop` (value 3) each tick. Result: did not block movement, and caused visual artifacts (eyes open during playdead animation).
3. **CharacterModes.AnimLock** — Considered but not tested; likely same timing issues as EmoteLoop.
4. **Zeroing gamepad input** — Zeroed `GamepadInputData.LeftStickX/Y` and processed float fields (`LeftStickLeft/Right/Up/Down`) on both `UIInputData.Instance()` and `Framework.Instance()` each tick. Result: no effect, game's native `MoveControl` reads input before the plugin's framework update runs.
5. **Zeroing keyboard input** — Zeroed `KeyboardInputData.KeyState[0x57/0x41/0x53/0x44]` (WASD VK codes) on both `UIInputData` and `Framework`. Result: no effect, same timing issue as gamepad.

### Notes
- The position hook approach blocks ALL position changes for the local player while active (including teleports, mount movement, etc.). This is acceptable because the block is only active during combat sim death state and is cleared on reset/stop.
- `GameObject.SetPosition` signature: `E8 ?? ?? ?? ?? 83 4B 70 01`
- `GameObject.SetRotation` signature: `E8 ?? ?? ?? ?? 83 FE 20`

---

## Draw weapon on target BattleNpc
Setting `CharacterData.InCombat = true` on an NPC when it becomes an active combat target does not cause the NPC to visually draw its weapon.

### Approaches tried (all failed)
1. **CharacterData.InCombat flag** — Set `character->CharacterData.InCombat = true` on NPC registration. Result: no visible weapon draw. The flag alone is insufficient.

### What would likely work
- Triggering a weapon draw animation via `ActionTimeline` (finding the correct timeline ID for weapon draw)
- Setting additional state: enmity table entries, AI controller state, or `Timeline.IsWeaponDrawn` (BitField bit 6 at `TimelineContainer` offset 0x34E)
- Calling a native weapon draw function if one exists on Character (none found in C# wrappers)

### Relevant structs/fields
- `Character` inherits `CharacterData` via `[Inherits<CharacterData>]`
- `CharacterData.InCombat` — BitField bit 1 at offset 0x34 of CharacterData
- `CharacterData.IsHostile` — BitField bit 0 at offset 0x34
- `Character.IsWeaponDrawn` — property, reads `Timeline.IsWeaponDrawn`
- `TimelineContainer.IsWeaponDrawn` — BitField bit 6 of `Flags3` at offset 0x34E
- `Character.WeaponFlags` (offset 0x1980) — `IsOffhandDrawn` at bit 0
- `WeaponState` (via `UIState.Instance()->WeaponState`) — player-only, has `SetUnsheathed(bool, bool, bool)`
- `EmoteController.PoseType.WeaponDrawn` = 1
