# Experimental cave terrain and registration

Open a saved paired Act I cave and choose Create > Extend ground (experimental).
The exporter edits HD terrain and DS1 together, carries LOD/model/physics dependencies
and writes a fresh workspace. Save and reopen it before gameplay testing.

Supported terrain is rigid, single-mesh and identity-transformed at 10 HD units per tile.
Growth adds at most 16 logical tiles per axis in positive X/Y. Unsupported partial
physics cuts reject the export. This is not arbitrary terrain sculpting or a biome shader.

Offline registration reads complete baseline tables, derives dense native row IDs,
placement, dimensions, DT1 slots and automap rows, and preserves unrelated table bytes.
The bundled TSV helper makes the commands standalone. Registration currently supports
Act I cave art, reuses donor labels and does not install files or launch the game.

For a boundary receipt produced by the separate boundary-authoring contribution:

```text
node scripts/PrepareBoundaryArea.cjs <baseline-data> <boundary-export> <fresh-output>
```

For a cave terrain export, use `PrepareCaveArea.cjs` with an explicit recipe. Both
commands reject duplicate/sparse IDs, unsafe paths and conflicting table changes.

Earlier disposable-profile tests confirmed added-ground walking, return travel and
Save and Exit/reload. The triangular ground gap remains unresolved. New candidates
require their own native rendering, collision, automap, travel and persistence checks.
Multiplayer remains untested.
