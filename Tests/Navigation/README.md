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
  walking once the crowd has gathered — the "circling" bug — and the update is cheap;
- a flood fill from where the player stands cuts off what cannot be walked to (the roof, the island),
  keeping the house floor and the terrace — the "mesh outside the bridge" seen in game.

Measured on the development machine (2026-11-13, tile 64 cells = 19 m, the game running alongside): the
scene builds in about 0.15 s. The crowd is run twice and the second run measured, because .NET re-compiles
hot code during the first seconds (first-run updates of up to 8–12 ms; none with
`DOTNET_TieredCompilation=0`): warmed up, 50 agents cost 0.34 ms an update on average, at worst 1–3 ms,
none over 3 ms. The plugin must warm the crowd up the same way before a fight (phase B). 34–40 of 50 reach
their exact spot; the rest stand just behind them, all within 8 m of the target.

Kneel spot (2026-11-15, `CombatSimulator/Battle/Rules/KneelSpot.cs`, linked): on open ground the spot is 0.25 m from the
body toward the approach and on the mesh; a body against the outside of a wall approached from inside gets a spot on the
walkable side within 0.3 m, and the straight walk through the wall is refused (the navmesh raycast
`NavMeshService.WalkableStraight` uses); a body inside the wall has no spot (kneels where it is); two bodies 0.6 m apart
get kneelers at least 0.8 m apart, and one body never gets a second kneeler; one who fell at the first spot is not knelt on.
