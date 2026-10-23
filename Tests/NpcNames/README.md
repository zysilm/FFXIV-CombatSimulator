# Battle NPC name resolution

`dotnet run --project Tests/NpcNames/NpcNames.csproj -c Release`

Links the production resolver and exact bundled data. Checks explicit-pair-only resolution,
the midge-swarm cross-category regression, legitimate many-to-many associations, localization,
deterministic deduplication, numeric-ID non-inference, unknown-name retention and malformed
or empty resource rejection. No installed game or private submodule is required.

The installed-game audit additionally joins these pairs to current BNpcBase/BNpcName/
ModelChara sheets; its results do not substitute for visual verification of every model.
