# Enemy skill geometry

Run `dotnet run --project Tests/MonsterSkills/MonsterSkills.csproj -c Release`.
The tests link the production CastType mapping and hit-shape math. The mapping rules were
measured on the Action sheet (5,138 monster-family rows plus every player damage action);
see the doc comment on `CastTypeMapping`.

These offline tests do not certify live animation, omen VFX or in-game timing.
