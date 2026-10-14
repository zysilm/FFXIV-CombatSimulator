# Rendered geometry checks

Run synthetic geometry/PBD tests:

```powershell
dotnet run --project Tests/RenderedGeometry -c Release
```

The fixtures check visibility, active shape replacement, all eight weight influences,
world scale (including reflection), unresolved bones, malformed inputs, PBD matrix
transpose, noncommuting chain order, parent-bone fallback and contribution reconstruction.
Thirty synthetic checks also cover partial-skeleton bind/current connection alignment,
primary-anchor ownership with preserved partial contributions, and source face limits
enforced before capture growth.

For optional checks against an installed game, first build the public plugin, then pass
its SqPack directory. This reads game files through Lumina; it never opens a game process.
The test invokes the actual built plugin's raw MDL parser by reflection.

```powershell
dotnet build CombatSimulator/CombatSimulator.csproj -c Release -p:DefineConstants=PUBLIC_BUILD
dotnet run --project Tests/RenderedGeometry -c Release -- 'C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack'
```

Installed-data verification on 2026-10-14 decoded 18 actual version 6 MDLs across
c0101–c1801 (29,068 triangles), and validated 18 same-race or supported ancestor PBD
routes. MDLs are selected from the base body or the first available equipment model.
These checks establish parser compatibility, not equality with shader output or a live
mod collection. Actor collection IPC, visibility masks and source stamps need runtime
integration evidence. GPU displacement and opaque post-deformer edits are outside this
CPU capture. Connected partial skeletons are captured when their public Havok reference
and current poses and named primary connections are readable; ambiguous or unsupported
partial bindings are explicitly omitted rather than approximated.
