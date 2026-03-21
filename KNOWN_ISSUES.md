# Known Issues

## Movement blocking on player death
When the player dies in the combat simulator, WASD keyboard keys and controller left joystick movement are not reliably blocked.

### Approaches tried (all failed)
1. **Position/rotation forcing per tick** — Saved death position, forced `GameObject->Position` and `Rotation` back every frame. Result: character shakes violently due to game engine fighting the correction each frame.
2. **CharacterModes.EmoteLoop** — Set `Character->Mode = CharacterModes.EmoteLoop` (value 3) each tick. Result: did not block movement, and caused visual artifacts (eyes open during playdead animation).
3. **CharacterModes.AnimLock** — Considered but not tested; likely same timing issues as EmoteLoop.
4. **Zeroing gamepad input** — Zeroed `GamepadInputData.LeftStickX/Y` and processed float fields (`LeftStickLeft/Right/Up/Down`) on both `UIInputData.Instance()` and `Framework.Instance()` each tick. Result: no effect, game's native `MoveControl` reads input before the plugin's framework update runs.
5. **Zeroing keyboard input** — Zeroed `KeyboardInputData.KeyState[0x57/0x41/0x53/0x44]` (WASD VK codes) on both `UIInputData` and `Framework`. Result: no effect, same timing issue as gamepad.

### What would likely work
- A native hook into `Client::Game::Control::MoveControl` input processing (no C# wrapper in FFXIVClientStructs, only IDA data references exist: `MoveControl::SplineController2D`, `MoveControllerSubMemberForMine`)
- Hooking at the input polling level before the game reads it (requires finding the right native function signature)

### Relevant structs/fields
- `UIInputData.Instance()` → `GamepadInputs` (offset 0x8), `KeyboardInputs` (offset 0x500)
- `Framework.Instance()` → `GamepadInputs` (offset 0x07B0), `KeyboardInputs` (offset 0x0A2C)
- `GamepadInputData.LeftStickX` (0x0), `LeftStickY` (0x4), `LeftStickLeft` (0xA4), `LeftStickRight` (0xA8), `LeftStickUp` (0xC4), `LeftStickDown` (0xC8)
- `KeyboardInputData._keyState` (offset 0x4, 159 entries of `KeyStateFlags`)
- `KeyStateFlags`: Down=1, Pressed=2, Released=4, Held=8
- WASD VK codes: W=0x57, A=0x41, S=0x53, D=0x44
- `Character->Mode` (CharacterModes enum): Normal=1, Dead=2, EmoteLoop=3, AnimLock=8
- `UIInputData.FilterGamepadInputs()` — strips all gamepad input (untested, would block buttons too)

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
