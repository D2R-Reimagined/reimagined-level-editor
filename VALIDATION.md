# Validation

## Automated checks

```powershell
dotnet build D2RLevelEditor.slnx
dotnet run --project tests/D2RLevel.Tests
```

The dependency-free assertion runner uses synthetic fixtures and does not require game assets or Granny. Coverage includes JSON preservation, DS1 versions 16–18, DT1 decoding, collision ownership/overlap, gameplay units and patrols, shared undo/redo, paired saves, rollback, external-change detection and deletion. It returns a nonzero exit code on failure. `dotnet test` does not run it.

## Optional checks with extracted assets

Gameplay prefab regression checks cover ownership closure, source-preserving capture, relative transforms/subtiles/footprints/patrols, fresh IDs, logical recipe remapping, changed-definition rejection, overlap/bounds/act/scale checks, shared undo/redo, nested transaction rollback, healthy save/reopen, bundled dependency closure, LOD aliases, declaration metadata, integrity checks, conflicting targets, path containment and unsupported components.

Use `--prefab-smoke` with an Act 1 town preset (for example `act1/town/townw1.json`), extracted `--data` and a fresh `--smoke-output`. It creates a disposable workspace, adds a direct Fallen recipe to that fixture's monster preset table, and assembles a real prop with two units, patrols and owned collision. It checks bundled assets, capture without source edits, empty search, invalid placement, scene/viewport undo and redo, package tampering, and save/reopen. It captures the library at 1220×850 and 1020×720 plus the capture dialog. This verifies local WPF and data round trips; it does not establish live-game behavior or every binary dependency format.

Entrance checks cover hidden-group translation, byte-exact undo/redo, cached orientations, paired-link fingerprint transactions, occupied/border/blocked/empty destinations, special-marker exclusion, directional Warp Id variants, reciprocal swaps, unused connections, malformed/generated/cross-act/ambiguous endpoints, Unicode/BOM/line-ending preservation, external dependency changes and fourth-file save rollback.

Use `--entrance-smoke` with `--preset <data>/hd/env/preset/expansion/mountaintop/mtntop.json`, `--data <data>` and a fresh `--smoke-output` folder. It inspects real Arreat Summit exit markers, verifies empty-space deselection and shared moves, then exercises preview/apply/undo/redo against a disposable four-area fixture using copied maps. It saves/reopens the complete workspace and captures WPF at 1280×850 and 1050×700. The fixture connections are not live-game travel proof. See [entrance editing](docs/entrances-and-exits.md).

The assertion runner covers level properties with workspace/base precedence, exact preset paths, duplicate and missing references, malformed tables, difficulty fields, shared Nightmare/Hell pools and source provenance.

Gameplay catalog checks cover act-local monster slots (including blank entries), explicit sparse object indices, current-act mappings, disabled and unmapped definitions, superunique resolution, duplicate keys, table-only mod overrides, stale-recipe rejection and shared placement undo.

Use `--gameplay-browser-smoke` with an Act 1 town preset to exercise the browser with real Akara, chest and superunique meshes. It creates a disposable workspace and verifies search clearing, unavailable entries, invalid coordinates, NPC/object placement IDs, undo/redo, immediate NPC previews, stale saved-table rejection and paired save/reopen. It captures the browser at 1120×800 and 950×650. Source assets stay unchanged; this is WPF/programmatic proof, not an in-game check.

Add `--level-properties-smoke` to the app command below with an Act 1 town preset. It creates a disposable workspace, verifies saved table refresh and difficulty controls, checks ambiguous/reusable contexts and inspector navigation, confirms source maps are unchanged, and captures the panel at 1100×700. This drives WPF controls programmatically; it does not test game runtime behavior.

```powershell
dotnet run --project tests/D2RLevel.Tests -- --audit 'C:\maps\town.json' 'C:\extracted\data' 'missing-assets.txt'
dotnet run --project tests/D2RLevel.Tests -- --probe 'C:\assets\prop.model' 'C:\tools\granny2.dll'
dotnet run --project tests/D2RLevel.Tests -- --texture 'C:\assets\prop_alb.texture'
dotnet run --project tests/D2RLevel.Tests -- --legacy 'C:\maps\town.ds1' 'C:\extracted\data'
dotnet run --project tests/D2RLevel.Tests -- --gameplay-audit 'C:\extracted\data\global\tiles'
dotnet run --project tests/D2RLevel.Tests -- --animation-audit 'C:\extracted\data' 'C:\tools\granny2.dll'
dotnet run --project tests/D2RLevel.Tests -- --strings-audit
dotnet run --project tests/D2RLevel.Tests -- --strings-update
dotnet run --project src/D2RLevel.App -- --preset 'C:\maps\town.json' --data 'C:\extracted\data' --granny 'C:\tools\granny2.dll' --smoke-output 'C:\scratch\editor-check'
```

Use a new output directory for each desktop smoke. Append `--features` for model explorer and DS1 interactions, or `--workspace-smoke --delete-smoke` for workspace discovery, saves, deletion and undo. Workspace checks require a preset and matching DS1 under their original game-relative paths; they copy maps beneath the output directory. Generated screenshots, settings and maps belong outside source control.

`--gizmo-smoke` with an Act 1 town preset and extracted assets checks X/Y/Z arrow dragging on real batched models and groups, exact axis constraints, and one-step undo/redo. It captures the selected model and active Y arrow. Synthetic rendering checks also cover different camera angles, negative movement, jitter, cancellation, terrain protection, NPC ground-only movement, and end-on axes. These checks drive WPF methods programmatically, not OS mouse input.

`--duplicate-smoke` with the same inputs checks Alt-axis fill previews, cancellation, model/group spacing, one-step undo/redo, save/reopen and geometry restoration after asset reload. It captures the live preview and a completed wall row. The core assertion runner verifies full-component cloning, unique identities, preservation of unknown fields and dependencies, rejection before mutation and transaction rollback.

`--box-selection-smoke` uses a disposable copy of the Act 1 pair to check rectangle rendering, batched selection, replace/add/toggle modifiers, deselection, cancellation and saved group expansion without scene edits. Synthetic checks cover partial bounds overlap, reverse dragging, near-plane clipping, hidden/locked objects and interaction with object/gizmo dragging. It also runs the movement and duplication checks.

`--animation-smoke` drives the model explorer's animation controls against a real character and captures its bind pose, an idle frame and a mid-stride frame; `--animated-model <data/...>` picks which character. `--gameplay-audit` walks every extracted DS1 and edits each patrol path in memory, checking the bytes revert exactly. `--animation-audit` loads every character rig and animation set, poses each mesh, and checks the result stays finite and near its bind silhouette — a character that collapses to a point or flies out of the world is a keyframe decode fault, and neither shows up as a non-finite value. Neither audit writes to its source files.

`--strings-audit` compares `src/D2RLevel.App/lang/en.json` and every language file against the `L.T` / `{l:T}` literals in the app sources, reporting coverage per language and failing on drift, unknown entries or placeholder mismatches; the default run performs the same comparison when it can find the repository root. `--strings-update` regenerates `en.json` and adds new blanks to every language file, removing entries the app no longer uses; building `D2RLevel.App` runs the same step, so commit the `lang/` files it touches. Starting the app with `--language <code>` exercises a language file for one run; smoke checks do not compare UI text, so they pass in any language.

## Verification boundaries

Authoring regression checks cover fresh v18 DS1 construction, floor strokes, flood boundaries, link fingerprint updates, insertion/deletion index remapping, first-path creation, portable project creation, inherited calibration, source preservation and stale-project rejection. Run the full assertion runner above.

`--authoring-smoke --smoke-output <fresh-folder>` on the app, together with `--preset`, `--data` and `--settings-file`, exercises actual ground controls and a newly created project. It captures the 1100px toolbar, menu visuals, DS1 window and workspace, then verifies save/reopen. Menu rendering is checked independently when desktop focus closes the popup; this is not OS input automation.

Research-only terrain probe:

```powershell
dotnet run --project tests/D2RLevel.Tests -- --terrain-export-probe '<terrain_lod0.model>' '<granny2.dll>' '<fresh-output-folder>'
```

This deliberately serializes the partial reader DTO into `partial-terrain-roundtrip.gr2` and writes a JSON report. The file is not a game-ready terrain asset and must not replace the source model. The probe separates local mesh serialization from the unresolved material/physics/game pipeline.

Local checks have exercised 205 assertions and published Windows builds with paired saving, linked collision, deletion and restoration of model geometry. Portable ZIP extraction and execution have also been checked locally. Historical screenshots and logs are local artifacts, not distributed repository files.

These checks do not establish compatibility on every machine, large-map performance or live-game correctness. Use a separate test mod and disposable character. Keep original game-relative paths and filenames; a different randomly selected town variant will not display your edits. Verify loading, placement, collision and units in-game. Terrain rendering is approximate and DT1/wall collision remains independent of owned floor overrides.
