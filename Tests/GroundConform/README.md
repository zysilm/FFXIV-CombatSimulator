# Ground conform checks

Pushes real skeletons into the ground through the production `GroundConformSolver`, from standing to
far below the floor in 80 steps, and checks every step. See `docs/GROUND_CONFORM.md`.

```powershell
dotnet run --project Tests/GroundConform -c Release
```

Needs the game installed (pass its `sqpack` directory as the first argument if it is not in the default
place). Skeletons are read from the `.sklb` files by `Tests/RagdollHullFit/Skeleton.cs`; the pose pushed
down is each skeleton's reference pose, not an animation.

Checked for every skeleton: no bone changes length; nothing is not-a-number. For every skeleton the
fit's own check lets through: nothing ends up under the ground, and the body and the limbs it stands on
never jump from one step to the next. For the human skeletons, by name: crouching keeps the pelvis at
the height it is held at, the feet where they stood and the knees going forward; pushed under, the body
lies on its front. At least 85% of all skeletons must pass the fit's own check.

Options: `--why` says for each skeleton left to sink what its core and limbs are and where it first
fails; `--core-trace` adds the core's tip and lift at every step; `--jump` lists bones that jump.
