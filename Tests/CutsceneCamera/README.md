# Cutscene camera probe

Offline reader for the camera work of the game's own cutscenes (`.cutb`), run against the installed game
(`C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack`). No game data is copied into the
repository. Commands (from this folder):

| command | what it does |
|---|---|
| `dotnet run -c Release` | report on three sample cutscenes + top-down BMP plots in `%TEMP%/cutscene-research` |
| `-- census [N]` | rig channel statistics over N cutscenes |
| `-- conv [N]` | Euler order / sign test of the rig rotations |
| `-- eid <path>` | dump a skeleton's bind-point table (`.eid`) |
| `-- branch <cutscene path>` | camera tracks of one cutscene and their Lua conditions |
| `-- bind [N] [lens\|seq]` | attach-point mapping test / lens channels / default sequence statistics |
| `-- shot <cutscene path> <timeline>` | dump every camera shot (C004) of one timeline |
| `-- curate [out.json]` | regenerate the curated follow-shot list (default `CombatSimulator/Resources/CutsceneShots.json`) |

File map: `CutbFile.cs` (container), `TmbTimeline.cs` (TMLB items, curves, track ownership, Lua conditions),
`CameraShot.cs` (C004 + rig evaluation), `Eid.cs` / `Bind.cs` (bind points, actor placements), `Sequence.cs`
(default sequence: unconditional tracks, last track wins), `Curate.cs` (the curated list). Research findings:
`docs/PROJECT_LOG.md` §6; the curated list: `docs/CUTSCENE_SHOTS.md`.

## Curated follow shots (`-- curate`)

Writes the list the plugin's Camera tab offers for victory and death shots. It is deterministic: running it twice on
the same game version gives a byte-identical file (sorted input, ordinal tie-breaks, no timestamps, invariant number
formatting, `\n` line endings). Takes about 30 s over the whole Cutscene sheet (~4000 cutscenes).

Pipeline:

1. **Candidates.** For every cutscene in the Cutscene sheet and every timeline, the default sequence is resolved
   (`Sequence.Resolve`). A candidate is a run of consecutive visible shots whose camera root is attached to the same
   actor A and that look at the same thing: *single* (aim in the rig or on A) or *two-person* (aim attached to
   another actor B). Shots whose eye is attached to a third actor are skipped. Runs longer than 15 s are cut on shot
   boundaries into windows of at most 12 s; a lone shot longer than 15 s is trimmed to its first 12 s. Windows of
   3–15 s are kept.
2. **Features**, sampled every 2 frames in A's own frame (A at the origin facing +Z; Hyur midlander male reference
   body for every actor, reference pose; B placed by the cutscene's C018 placements relative to A):
   framing from the frame height covered at the subject, `h = 2·d·tan(fov/2)` (close-up < 0.8 m, medium < 1.8 m,
   full < 4 m, else wide); angle from the eye's elevation seen from the subject's head (low < −8°, high > 12°);
   side from the eye's azimuth around A (front < 55°, back > 125°, else side); motion per shot from its first and last
   sample (push-in / pull-out: frame height ×0.85 / ×1.18; orbit: ≥12° around the look point, left/right by the
   eye's move along the camera's right; crane: eye height change ≥ max(0.25 m, 0.12·d); pan: view turns ≥8° with the
   eye nearly still; tracking: camera still relative to A but A's placement moves > 1 m; else static), the segment
   takes the motion of its longest shot, `slow` when that move is under 2.5× its threshold; roll = c117 or the
   up-vector roll, whichever is larger; subject = A's head or chest (single), B's head (two-person).
3. **Filters**, in this order (the run prints how many are left after each): *humanoid* (every attach point is a
   humanoid bind point; look point within 2.6 m height and 3 m of A for single shots; B 0.3–10 m from A and within
   1.5 m height; camera within 14 m; subject 0.25–25 m from the eye), *fov* (15–80° throughout), *ground* (eye
   ≥ 0.1 m above A's feet), *roll* (≤ 25°), *smooth* (inside a shot: eye ≤ 0.4 m/frame, view ≤ 6°/frame, FOV
   ≤ 2°/frame, no step > 6× the shot's median step), *in-frame* (subject inside the FOV cone in ≥ 85% of samples).
4. **Corpse suitability**: true when no attach point is a root-height point (bind points on `n_root`, e.g. 76, 42,
   71); bone points (head 1/26/27, chest 28, belly 18, weapons 77/78, …) follow a body lying on the ground.
5. **Two-person roles**: the entry is about the face it shows. Camera in front of / beside A with A in frame →
   `follow: subject, lookAt: other`. Camera behind A or A mostly out of frame (over the shoulder, showing B's face) →
   `follow: other, lookAt: subject`.
6. **Selection**: score = 1 − 0.5·off-centre − 0.3·jerkiness + 0.15 (moving camera) + 0.2 (4–12 s) − 0.1 (roll)
   − 0.03 per cut beyond two. Classes are kind (single / two-follow-subject / two-follow-other) × framing × motion.
   Pass 1 keeps the best of each class, pass 2 a second one; then two-person entries are topped up to 20 per
   direction and corpse-suitable ones to 40; at most 150 in all. At most one entry per cutscene file, and identical camera work reused by another cutscene (variants) is taken once.
7. **Names**: from the features, e.g. `Slow push-in, low close-up, profile`, `Follow other, see subject, pan full`.
   Duplicates get the expansion tag from the path prefix (`ffxiv` ARR, `ex1` HW, `ex2` SB, `ex3` ShB, `ex4` EW,
   `ex5` DT) and then a number.

### JSON format (`version` 1)

```jsonc
{
  "version": 1, "source": "...", "note": "...",
  "shots": [{
    "id": "kingmg10110-t7-s6",            // <cutscene file name>-t<timeline>-s<first shot>, stable for a game version
    "name": "Slow crane down, close-up",  // dropdown label
    "category": "single/close-up/crane-down",
    "tags": ["single", "close-up", "crane-down", "slow", "eye-level", "front", "DT"], // + "follow-subject"/"follow-other", "roll", "cuts", "corpse"
    "cutscene": "ex5/kingmg/kingmg10110/kingmg10110", // Cutscene sheet Path; file = cut/<path>.cutb
    "timeline": 7,                         // index of the CTTL chunk in file order
    "firstShot": 6, "lastShot": 6,         // C004 index among the timeline's C004 entries, file order
    "shots": [{ "shot": 6, "from": 640, "to": 780 }], // visible part of each shot, timeline frames (30 fps), [from, to)
    "startFrame": 640, "endFrame": 780, "duration": 4.67, "cuts": 0,
    "kind": "single",                      // or "two-person"
    "follow": "subject", "lookAt": null,   // two-person: "subject"/"other" and the opposite
    "followActor": 11, "lookActor": null,  // cutscene actor indices (root attach / aim attach)
    "corpseSuitable": false,
    "attachPoints": [76],                  // bind point ids used by the root/eye/aim slots
    "features": { "framing": "close-up", "angle": "eye-level", "side": "front", "motion": "crane-down", "slow": true,
                  "motions": ["crane-down"], "roll": false, "maxRoll": 0, "fovMin": 40, "fovMax": 40,
                  "distance": 0.96, "quality": 1.22 }
  }]
}
```

Only references and derived numbers are stored; the camera curves themselves are read from the user's game install
at runtime.
