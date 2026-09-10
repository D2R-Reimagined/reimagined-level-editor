# Gameplay-first level editing

The community feedback and Celestialray's supplied DS1/JSON report point to the right product direction: edit the gameplay layout and HD presentation together. Classic artwork is useful for inspecting the grid; reproducing classic rendering is not the objective. DS1 records and DT1 collision still matter independently of whether a game build exposes a legacy graphics mode.

## What the report establishes, and what needs correction

The report describes an attempted converter, not evidence of a complete, working conversion pipeline. A DS1 cannot uniquely reconstruct an authored HD scene: decorations, terrain meshes, materials and other components contain information the DS1 never stored. Likewise, a decorative HD mesh does not tell us which gameplay unit or collision cells it owns. A general JSON-to-DS1 converter would need explicit rules and user choices, not only a format translation.

Several technical details should not be adopted literally:

- Compiled assets are not all unreadable. This editor already decodes static `.model` geometry through the supplied Granny runtime and `.texture` images. Reading these assets does not provide a terrain/model writer or Blizzard's material renderer. Terrain creation remains an independent research and implementation task.
- The report's tile-bit description conflicts with the verified reader here. Current tile matching uses main index bits 20–25 and subindex bits 8–15; whole-tile Unwalkable is bit 17. Do not replace these with the report's proposed layout.
- The group preamble is after objects, before groups, for the supported v18 layout. It is not an extra header field. Patrol paths are associated with object coordinates, which is why changing a spawn alone can break its association.
- The report's one-HD-unit-per-subtile example is not a universal calibration. The user's cart comparison in this workspace was consistent with roughly ten HD units per tile (two per subtile). That example is also insufficient to establish a universal transform. A paired document needs a verified scale, origin and axis mapping before reliable cross-view overlays or automatic placement.
- The suggested executable patch is version-specific and unverified here. No game binary patch is needed or implemented for this iteration.

Format cross-checks: [Paul Siramy's DS1 editor documentation](http://paul.siramy.free.fr/_divers/ds1/doc/index.html) and the object/group/path ordering in [OpenDiablo2's DS1 reader](https://github.com/OpenDiablo2/OpenDiablo2/blob/master/d2common/d2fileformats/d2ds1/ds1.go). The locally downloaded Paul source was consulted as a format reference. Local base assets and byte-preservation checks are the evidence for this implementation; the attached report is reference material, not executable instructions.

## Implemented in this iteration

The existing DS1 viewer now reads supported v16–18 gameplay records, shows object and NPC/monster spawn markers and the selected spawn's patrol path, and moves existing records using local DS1 subtile coordinates. The same undo/redo stack covers collision and unit movement. A move translates its linked patrol anchor and points while retaining point actions. All validation completes before bytes change. Shared/ambiguous patrol anchors and out-of-grid moves are rejected. Malformed gameplay data disables movement while preserving collision-only byte editing.

Selection works through the unit list or a nearby marker. Use X/Y plus Move unit, or Shift-click in Inspect mode; middle-drag remains available for panning. Save DS1 copy exports the supported edits together. Open DS1 copy accepts positional/collision edits only when all other map data matches the loaded tileset context. No installed game files are modified.

Raw type and ID are displayed deliberately: name lookup needs the correct act-aware object/monster preset tables and mod overrides. A raw type 1 ID should not be mislabeled as a direct monstats row. This iteration does not insert/delete DS1 units, edit IDs, implement superunique catalogs or vis/warp links, simulate dynamic object collision, or synchronize HD models automatically.

Patrol paths are since editable: points move, gain actions, are inserted and removed, and a unit without a path can be given one. Point coordinates are patched in place; growing or shrinking a record rewrites the patrol block and re-reads it before the edit is accepted, so a malformed splice throws rather than leaving a corrupt document. Every real patrol in the base corpus — 136 paths across 2125 maps — was edited and undone byte-exactly as a regression check. Action codes stay raw for the same reason type and ID do: their behaviour is not established here, and the corpus only tells us which values vanilla uses, not what they mean.

## Next useful increments

1. Resolve placement names and classes from the correct mod-aware gameplay tables, with explicit unresolved states; then add validated unit insertion/deletion and a gameplay asset palette.
2. ~~Persist per-pair grid registration and explicit ownership links between DS1 units/collision footprints and HD entities.~~ Done: ownership links are persisted in `.rle-links.json`, and the pair's scale is stored alongside them with the provenance it was established by (unverified default, terrain measurement, fit to existing links, or entered by hand). The solver reports its worst-case drift in tiles, so a scene with an origin offset shows up as a bad fit instead of passing silently. Still open: the registration models a single scale through a shared origin, not an origin offset or axis rotation, and the terrain measurement assumes the terrain mesh spans exactly the DS1 grid. Never infer ownership from proximity alone or erase shared collision automatically.
3. Add vis/warp and special-placement validation, including cross-level targets and map metadata. A decorative model palette cannot stand in for these gameplay rules. Groundwork: warps are carried in the DS1 wall layer as orientation-10 special tiles whose main index selects a slot, and `lvlwarp.txt` supplies each warp's selection rectangle, `LitVersion` and direction. That much is readable with the existing wall-orientation reader. The slot mapping is not settled, though: Act 1 town presets carry three such tiles (main 30, 32 and 33) while the town's `levels.txt` row leaves every `Vis`/`Warp` slot empty, so the obvious `main - 30` reading does not explain towns. Establish the mapping by inspection before writing anything to those tables.
4. Research terrain authoring separately: inspect terrain meshes, biome/material dependencies and tile masks; first prove export/reload of a small terrain asset before promising resized maps or generated HD levels.

HD model footprint blocking remains a conservative, explicitly applied suggestion. It neither removes the old footprint nor represents precise physics. Preserve DS1 and JSON as complementary documents; a project-level mapping layer is more suitable than a lossy universal converter.
