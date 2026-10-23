# Virtual enemy look-at

Run `dotnet run --project Tests/HeadLookAt/HeadLookAt.csproj -c Release`.
The tests link production rotation math and target/ownership policy. They cover bind axes,
animated facing, yaw/pitch limits, local/model delta conversion, invalid targets, defeated-player
priority over companions, stage/grab ownership bypass after defeat, dead observers, missing
entity identities, self-targeting and returning to ordinary combat targeting after revival.

The feature defaults off. While enabled and the simulation remains active, the defeated
player remains the target for living virtual enemies regardless of victory/grab/control
ownership. Dead enemies release the pose to their ragdoll. Native addresses are checked
against the current object-table identity before bone reads/writes.

The bone picker lists current enemy/player skeleton bones with a case-insensitive search.
Look-at runs after ordinary bone modifiers and before prepared-pose observers. Grab target
observers read the resulting hand position; selecting the neck/chest therefore cannot leave
the servo targeting the pre-look-at hand pose. Disabled/inactive mode only retains a bounded
two-second release window, then stops scanning actors.

These offline tests do not certify live animation, GUI interaction or native grab visuals.
