# Navigation mesh (DotRecast)

```powershell
dotnet run --project Tests/Navigation -c Release [tileCells]
```

Builds the plugin's navigation mesh (`CombatSimulator/Navigation/NavMeshBuild.cs`, linked) over a scene
made to reproduce the movement bugs reported in game (docs/AI_MOVEMENT_REWRITE.md): open ground, a
house with a door and a walkable flat roof, a ramp to a terrace, and a raised island with sheer sides.

Checks:
- neighbouring tiles are linked (a long path over open ground reaches its end);
- inside the house the floor is found with a small vertical search, never the roof (the roof is its
  own layer, found only from on top of it) — the "actor pops onto the roof" bug;
- a path into the house goes through the door and stays on the floor;
- the ramp leads to the terrace; the sheer island is not reachable — the "spawned off the map" bug;
- a DetourCrowd of 50 agents closes on a target, those that reach their spot stay still and none keeps
  walking once the crowd has gathered — the "circling" bug — and the update is cheap.

Measured on the development machine (2026-11-13, tile 64 cells = 19 m): the scene builds in about
0.15 s; the crowd update for 50 agents averages 0.6–0.8 ms. A handful of updates in the first seconds take
up to 8 ms while .NET re-compiles hot code (with `DOTNET_TieredCompilation=0`: none over 3 ms, worst
0.9 ms, mean 0.37 ms). 34–40 of 50 reach their exact spot; the rest stand just behind them, all within
8 m of the target, the busiest moving 0.3 m in the last second.
