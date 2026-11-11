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
never jump from one step to the next. For the human skeletons, by name, with their arms let down from
the reference pose: crouching keeps the pelvis at the height it is held at, the feet where they stood
and the knees going forward; on all fours the back is level, knees and hands are on the ground, hands
under the shoulders, hips up over the knees, the face looks ahead and down, the feet trail toes down
behind the ankles, and no bone of the body (fingers included) is under the ground, with the numbers
printed beside those of a crawl made by hand; pushed under, the body is low and on its front but not
flat, and pushing deeper changes nothing. Then with legs and arms swung 24 degrees either way, a degree
at a time, at four depths (crouching, between, on all fours, as low as it goes): no bone moves more
than 4% of the height for one degree, and hands and knees still travel. And with the left leg drawn up
as a walk draws up the leg it swings forward, a degree at a time, on all fours and as low as it goes:
that knee is lifted and its hip is up, its shin comes up, the hand across from it is lifted, the other
knee and hand stay down, the hips shift over the knee that stands, and no bone moves more than 4% of
the height for one degree. At least 85% of all skeletons must pass the fit's own check.

Options: `--why` says for each skeleton left to sink what its core and limbs are and where it first
fails; `--core-trace` adds the core's tip and lift at every step; `--jump` lists bones that jump over
the push and `--swing` those that jump over the swing; `--trace m0009` (last argument: the end of a
skeleton's label) prints stage, tip and lift at every depth, and its limbs.
