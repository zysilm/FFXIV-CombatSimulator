# Virtual enemy name/appearance mapping

## Current policy

BNpcBase is the appearance identity. BNpcName is a separate localized name table. Only an
explicit pair in the bundled `Npcs/Data/BNpcLinkNoGubal.csv` may give a battle appearance a
name. One base may legitimately have multiple names, and one name multiple appearances;
neither association degree nor matching numeric IDs establishes correctness.

The former degree-based filter and legacy `B:` labels from NpcNames.json no longer name
battle appearances. A base with no usable direct pair/localized name remains in the catalog
as `Battle NPC <BaseId>` with NameId 0. Search by Base/Model ID, model categories, valid
same-name variants, popular/recent appearance IDs and the existing spawn identity path are
preserved. Existing recipes with explicit Base IDs still resolve those appearances. A recipe
that depended on an unproven name/occurrence can no longer rely on that false association.

ENpcResident's same-ID name takes precedence for event appearances; the old event label is
used only if that name is unavailable. Unreferenced monster models remain discoverable by
ModelChara ID through the existing direct-model path.

The upstream NuGet 4.3.7 assembly lacks the NoGubal resource despite declaring its name.
Its exact source-commit CSV is therefore bundled with provenance and its upstream license.
There is no runtime download or substitution of the broader discovery graph. A missing,
malformed or empty resource logs an error and falls back to ID-labelled appearances.

## Verification, 2026-10-23

The production-linked `Tests/NpcNames` executable passes 20,298 checks over the 20,280 pairs,
localization, duplicate/variant handling, strict parsing and the known swarm regression.
CI runs this without a game install or private submodule.

An installed-game audit instantiated the actual built NpcCatalog with an offline IDataManager
adapter over local Lumina sheets. The plugin's own embedded resource was read successfully.
It produced 54,157 catalog entries, including 21,702 battle variants: 17,761 named variants
and 3,941 ID-labelled appearances. All 20,307 available battle base rows were retained;
every named battle entry matched an explicit pair and current localized name, and every
entry's model/category matched its base row. `midge swarm` resolved only to Base 136,
ModelChara 58; Base 19257/19271 remained available without that misleading label.

These checks establish data/identity consistency, not visual certification of every native
spawn, mod replacement or historical upstream source record. The source snapshot predates
some current game rows; missing names intentionally remain unknown until direct evidence is
available. Tests and bundled source do not use the live game process.
