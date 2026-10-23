# Battle NPC name associations

`BNpcLinkNoGubal.csv` is an unmodified source snapshot from Critical-Impact/LuminaSupplemental,
commit `038c4fd8c59ef1d06cde7fb69ac579946de58053` (the commit recorded by package 4.3.7):

https://github.com/Critical-Impact/LuminaSupplemental/blob/038c4fd8c59ef1d06cde7fb69ac579946de58053/src/LuminaSupplemental.Excel/Generated/BNpcLinkNoGubal.csv

20,280 explicit pairs, columns `BNpcNameId,BNpcBaseId`. Raw upstream SHA-256:
`3e70cd97ea13b059876b27a215df0299bdb8afeae7741d7aae00859a10f650c0`.
The upstream GPL-3.0 license is retained in `LuminaSupplemental.LICENSE`; both the data and
license are embedded into the plugin. This notice applies to that upstream data snapshot.

The upstream NoGubal generator uses source spawn pairs and excludes the Gubal discovery
graph. Package 4.3.7 declares this resource name but omits the actual CSV from its assembly,
so the plugin bundles it directly. No runtime request or heuristic fallback is used.
Source inclusion is not a claim that every upstream record is visually verified for every
game patch. Updates must preserve provenance and rerun offline/current-game checks.

Names are resolved through the client's localized BNpcName sheet. Missing source rows or
unavailable names keep the base appearance searchable with an ID label. Legacy B: labels
in NpcNames.json are not used as evidence, even when the IDs happen to match.
