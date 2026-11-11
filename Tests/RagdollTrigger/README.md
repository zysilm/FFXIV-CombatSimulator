# Ragdoll trigger checks

Plays synthetic deaths through the production `RagdollTrigger` at 30, 60 and 144 frames a second and
checks when each mode starts physics: the fixed delay, one frame before the death animation ends, at
90% of it, when the body falls fastest, and once it has landed. Also: a death without a fall, the
stance the character died in being near its loop end, an animation cut short, nothing readable, and
the 30 second limit.

```powershell
dotnet run --project Tests/RagdollTrigger -c Release
```
