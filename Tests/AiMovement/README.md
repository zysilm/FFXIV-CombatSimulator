# Enemy movement math

Run `dotnet run --project Tests/AiMovement/AiMovement.csproj -c Release`.
Links the pure helpers in `CombatSimulator/Simulation/EnemyMovementMath.cs` (terrain follow,
slot rings, hitbox spacing, turn rate, threat, strafe goals) and checks them offline. In-game
behaviour (animation, navmesh, real hitbox radii) is not covered.
