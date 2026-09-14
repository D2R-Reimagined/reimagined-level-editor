# Creating new levels: investigation and implementation plan

Original plan prepared 2026-09-10 against editor commit `fc68294`. The execution status below records the implemented subset; later milestones remain proposals and have no in-game compatibility claim.

## Execution status — 2026-09-10

The first implementation batch now adds new project creation, a fresh v18 DS1 writer, fixed template dimensions, explicit DT1 context, floor pencil/erase/picker/fill/rectangle, template unit insertion, unlinked unit deletion, first patrol creation, safe staged project publication and paired saves. Insert/delete and floor edits refresh link metadata in the same undo transaction. Known standalone HD scenery can be cleared while retaining terrain/environment and unknown components. Effective DT1/palette/table context and referenced mod HD overrides are carried into the project. The main toolbar is grouped into File, Create, View and Assets.

Local real-asset checks used Act 1 Cave Treasure 2 (`caveroom2`, LevelId 13), a 25×25 preset. They exercised project creation, actual ground controls, a unit and patrol, save/reopen, source preservation and toolbar/menu rendering. This is local editor evidence; no game entry/return test was performed. New gameplay is blank and has no entrances, so the playable-arena acceptance gate remains open.

The full assertion runner passes 366 checks. The self-contained local application was republished to `artifacts/app`, and the authoring smoke was run from that executable. No release, deployment or live mod replacement was performed.

The terrain experiment decoded and reserialized the cave's 947-vertex, 1511-triangle mesh through LSLib. The result decodes locally, but the DTO preserves only meshes and selected mesh metadata. It omits unknown root/material data and has no physics or biome writer. It is a research `.gr2`, not an exporter exposed to users. A compatible asset writer and an in-game check remain prerequisites for sculpting.

Still open: general wall/special-tile authoring, supported entrances, verified paired HD ground kits, complete coordinate registration, reusable gameplay prefabs, independent area registration, surface/height export, resize and procedural generation. Milestones below remain the roadmap; the locally completed subset does not close their game-validation gates.

## Recommended direction

Build a unified, preset-level authoring workflow: **New Level → draw ground → place scenery and gameplay objects → validate → export → play**. Start with a small, fixed-layout arena using existing D2R assets and a known game entry point. Expand into independent level registration and custom terrain once their export paths have been demonstrated.

There are three distinct deliverables:

1. **A new authored layout in an existing level slot.** Fastest route to testing new ground, objects and collision in-game. This is a real custom layout, but does not add a new area ID.
2. **An additional area connected to the world.** Requires level/preset registration, valid entrances and exits, and target-build verification.
3. **Custom terrain geometry and surface painting.** Requires exportable HD assets and a deliberate relationship to DS1 gameplay. A brush preview does not establish game support.

The first should be useful on its own. The third needs an early feasibility experiment, rather than being left until after building a large brush UI.

## What the current editor already supplies

| Capability | Evidence in current source | Authoring gap |
| --- | --- | --- |
| HD placement and transforms | `PresetDocument.AddModel`, model explorer, viewport | Reusable placement recipes, snapping and repeated stamping |
| Gameplay editing | `Ds1Gameplay`, `Ds1Paths` | General unit insertion, act-aware object catalogs; deletion currently has a linked-object path |
| Ground inspection | `LegacyFloors`, `TerrainPreview` | Actual floor/wall painting and an HD terrain output path |
| Collision and ownership | `Ds1CollisionDocument`, `PlacementLinks` | Brush recipes that change tiles and owned collision together |
| Spatial registration | `GridCalibration` | Scale only today; no origin offset or axis mapping |
| Undo, paired persistence | `EditHistory`, `WorkspaceSceneSession` | New documents, structural edits, asset/table output and recovery |
| Grouping | `AssetGroups` | Groups are selection/translation metadata, not portable gameplay prefabs |

Important constraints established by inspection:

- `Ds1CollisionDocument` loads existing bytes with fixed dimensions and layers. Its collision brush changes the Unwalkable flag, not tile selection. A supported structural writer is needed to create maps, change dimensions and insert records.
- `LegacyFloors` requires a unique `lvlprest.txt` filename match and nonzero `LevelId`, followed by `levels.txt` and `lvltypes.txt`. An unsaved new map cannot depend on filename inference for its tileset.
- `SceneWorkspace.Scan` discovers existing JSON/DS1 pairs; `WorkspaceSceneSession` requires both files to exist. Neither is currently a New Level workflow.
- Link metadata depends on DS1 structure and unit indices. Insertion, resize and tile changes need controlled remapping and updated fingerprints, or valid editor operations will break links.
- `TerrainPreview` projects legacy graphics onto an existing mesh and still uses a hardcoded approximate 10 HD units per tile. It is not a terrain writer or a reproduction of the game's surface blending.
- The available `townwest.json` terrain entity references `terrain.model` and `terrain.physics`, alongside terrain and physics components; its dependencies also include biome and terrain textures. This establishes an asset dependency problem, not the exact role or necessity of every file at runtime.
- Extracted environment directories are available under `C:/dev/d2r/base-files/data/data/hd/env`, including biome, tilemasks, model and preset directories. Completeness for any selected map has not been checked.

## What “drawing terrain” should mean

Implement terrain in ascending levels of complexity:

| Tool | User-visible behavior | Required output |
| --- | --- | --- |
| Ground tile brush | Paint a path, floor or blocked boundary | DS1 floor/wall cells using an explicitly selected DT1 tileset |
| Paired ground stamp | Place a tested ground/wall piece with matching gameplay | HD entities plus DS1 tiles/footprints and editor ownership |
| Surface brush | Paint grass, dirt or stone with transitions | A verified material/mask/texture export route, plus gameplay recipe when appropriate |
| Height brush | Raise, lower, flatten and smooth ground | A verified mesh pipeline and a decision on physics, collision and supported elevation |

Start with the first two. Do not assume every stock terrain mesh is modular or can be seamlessly tiled. Build a small tested kit and label its supported rotations, sizes and edge connections. Existing large terrain meshes can remain a scaffold until a reusable ground kit is proven.

DS1 remains the gameplay source for the supported layout. Visual elevation must not silently imply slopes, bridges, stacked walkable floors or arbitrary navigation. Test these separately before exposing them as supported features. Similarly, removing a DS1 override does not remove blocking intrinsic to a DT1 tile; the initial brush must choose suitable existing tiles.

## Proposed architecture

Introduce a `LevelProject` above the current paired documents. Store editor-only authoring data in a versioned manifest, leaving game files in their native formats.

The manifest should carry project identity, output paths, act and explicit preset/level/tileset context, dimensions, coordinate registration, template provenance, stable placement identities, prefab instances, terrain source data and required dependencies. Keep external asset roots as local settings so projects remain portable. Distinguish editor scene identity from game area/preset IDs.

Use a supported semantic DS1 representation plus a serializer for new/structurally edited maps. Retain the current byte-preserving path for ordinary existing-map edits. Start with one known supported output version, probably v18 after fixture verification; reject unsupported structural transformations rather than silently discarding unknown records. New documents need intentional defaults for layers, flags, filenames, groups and patrol blocks.

Represent each brush stroke or object placement as one validated command spanning JSON, DS1 and metadata. Preview without mutating saved state; commit once; cancel on Escape, lost capture, workspace change or shutdown. Undo must restore record order, dependencies, IDs, paths and collision ownership as well as visible geometry.

Replace scattered coordinate arithmetic with one registration service. New projects get a canonical grid/origin; imported scenes retain their established calibration. Add offset/axis support only where required and tested. Terrain projection, picking, NPC grounding, snapping, footprints and export must use the same mapping.

Authoring recipes should distinguish decorative scenery, blocking scenery, interactive objects and NPC/monster placements. Only recipes with verified gameplay mappings create DS1 units. Resolve catalogs using workspace overrides and the correct act/type tables, rather than treating every displayed model or monster name as a valid raw placement ID.

## Milestones and acceptance gates

### 0. Prove a target and a terrain path

Choose one small, non-quest-sensitive preset and one environment kit. Record the game/mod build, exact loaded variant, table context and complete dependencies. Make a separate test-mod export.

Run two bounded experiments:

- **Layout experiment:** load a controlled replacement pair, change a floor selection, add a gameplay placement using a minimal fixture/tool, and establish entry/return behavior.
- **Terrain experiment:** first reuse a stock ground piece; then export one deliberately changed or generated flat ground mesh with a simple surface. Investigate required vertex data, materials, bounds, dependencies, biome/tilemask interactions and the referenced physics asset. Compare editor reload with actual game rendering and movement. Do not assume the existing reader can write these assets.

Deliver a compatibility note and tiny repeatable fixture/export recipe. If custom mesh export fails, keep the first release on verified stock assets and identify the exact missing step. Do not call generated terrain supported until the game loads it correctly.

### 1. New Level foundation and structural DS1 editing

- Create/open/save a `LevelProject`, including an empty workspace and unsaved document lifecycle.
- Offer a validated template, act/environment context and supported map size. Initially constrain dimensions to the tested template if terrain does not yet support resizing.
- Implement new DS1 construction, explicit tileset context, floor/wall edits and insertion/removal of supported gameplay records.
- Preserve/remap links, patrol associations and collision baselines through structural edits. Introduce stable editor IDs and map them to serialized indices.
- Add staged create/save with no accidental overwrites, external-change detection and recovery for new files as well as existing ones.

**Gate:** create a pair without an input DS1, save/reopen it, verify dimensions/layers/records, and undo/redo structural edits without link corruption. Existing-map preservation checks remain intact.

### 2. Ground brushes and the first playable authored layout

- Add a tile palette with thumbnails, brush size, pencil, rectangle, fill, erase and tile picker.
- Paint through a top-down view or the 3D grid using the shared coordinate service.
- Show walkability and unresolved/variant-dependent tiles. Keep tile choice separate from unsupported force-walkable behavior.
- Add the first verified HD ground/wall stamps and edge rules. Make each paired stamp one command.
- Export to the known test slot. Generate a manifest of changed files and validate before replacement.

**Gate:** draw a connected route and blocked boundary, place stock scenery, export, enter the map, walk the route and confirm the boundary in-game. Record any HD/gameplay mismatch. A DS1-only preview passes the writer milestone, but not this paired-ground gate.

### 3. Gameplay palette and reusable prefabs

- Add validated interactive-object and NPC/monster insertion, naming and deletion.
- Support prefab definitions containing HD entities, optional DS1 units, tile patches, collision ownership and dependencies.
- Add duplicate/stamp, grid snap and placement preview; allow rotation only for recipes whose tile orientation and gameplay effects are implemented.
- Preserve overlapping collision, avoid duplicate interactive visuals, and handle unit insertion/reordering without breaking another object's links or patrol.

**Gate:** place a decorative prop, a blocking prop, one supported interactive object and one supported NPC/monster; move, duplicate, delete, undo and reopen them. Verify interaction/spawning and collision in-game. Town services, quests and scripted bosses require separate recipes and validation.

### 4. Independent area registration and connections

- Add a registration editor for the required `levels.txt`, `lvlprest.txt`, `lvltypes.txt` and, where applicable, `lvlwarp.txt` changes, resolved against the current mod.
- Allocate/check IDs, filename references, tile masks, dimensions and level type; preserve unrelated table columns and rows.
- Investigate and implement the supported Vis/Warp/special-tile mapping. Current project notes explicitly leave town slot mapping unresolved; do not generalize a guessed formula.
- Validate reciprocal entry/return paths, spawn positions, world layout constraints and required strings/automap behavior for the chosen area type.
- Stage table and asset changes together with a reviewable diff and recovery manifest. Establish whether the target workflow requires compiled table regeneration.

**Gate:** add an independently registered test area, enter and leave it, reload the game and repeat. Document save compatibility and test multiplayer transitions if the feature will be shipped for multiplayer. Replacing an existing slot does not pass this gate.

### 5. Native surface painting and terrain sculpting

Proceed from milestone 0's proven asset recipe. Choose whether the first backend uses reusable pieces, an external model conversion step, or a native writer; do not commit to a native writer before feasibility is established.

- Store editable terrain source separately from compiled game output.
- Implement flat surface painting and blending first, then bounded height editing if the game experiment supports it.
- Export required geometry, material/texture/mask assets and any demonstrated physics dependency. Rebuild only changed chunks once correctness is established.
- Validate seams, normals, UVs, bounds, camera behavior, lighting and ground alignment. Test gameplay across slopes/edges and interactions with collision/projectiles rather than relying on the editor mesh.

**Gate:** a newly painted surface and a deliberately changed terrain shape survive export, editor reopen and game reload with documented supported behavior. Advanced water, cliffs, caves and stacked spaces remain separate capabilities.

### 6. Broader authoring

After fixed presets work, add resize with explicit crop/anchor rules, larger environment kits, scatter tools with deterministic seeds, prefab libraries, procedural room connections and a world graph. Procedural `LevelId=0` contexts and generator-specific tables need their own implementation and acceptance criteria.

## Verification strategy

- Synthetic DS1 writer fixtures: empty/full maps, layer counts, boundary sizes, malformed input, insertion/removal and first patrol creation.
- Regression corpus: preserve existing unknown JSON and DS1 data; round-trip supported structural edits; migrate link sidecars deliberately.
- Command tests: overlapping ownership, canceled strokes, rejected destinations, duplicate IDs, unit index shifts, shared patrol anchors and stale asset/table context.
- Save tests: partial replacement failure, external changes, fresh-project failure cleanup and interrupted export recovery. Multi-file replacement is not crash-atomic; recovery must be explicit.
- UI checks: new/empty workspace, palette navigation, long strokes, undo/cancel, reopen and scene switching. Measure brush latency and memory on a representative larger fixture before choosing chunk sizes or renderer changes.
- Game evidence: exact build and map variant, exported hashes, screenshots/video, movement/interaction/entry-return observations. Local parse/build/render success is recorded separately.

No implementation tests or game experiments were run for this planning document.

## Suggested first implementation batch

Begin with milestone 0, then deliver the smallest complete authoring foundation: `LevelProject`, explicit tileset context, a supported DS1 builder/writer, a template-backed New Level flow, a floor pencil and safe paired export. Keep the initial UI to one environment and the tested size. The first demonstrable outcome should be **a newly authored small arena that can be entered and walked in-game**.

General gameplay prefabs and independent area registration follow that proof. Research terrain export from the beginning; build the polished surface/sculpt brushes after the output contract is known. The structural DS1 and editor work is reasonably bounded; HD terrain/physics and new-area integration carry the highest uncertainty. Estimate calendar dates after milestone 0 identifies the actual export route and runtime constraints.

## Research references and confidence

- [DS1info2Unity project](https://github.com/MilesTeg97/D2R-DS1info2Unity/blob/main/README.md): its author describes DS1 gameplay data and separate HD JSON presentation. This supports maintaining complementary documents; it is not proof of an automatic terrain converter.
- [Unity D2R Scene Editor](https://github.com/pairofdocs/Unity-D2R-Scene-Editor): existing placement-oriented reference. Its advertised scope does not establish terrain writing.
- [D2R Modding tools, maintained by Bonesy](https://www.d2rmodding.com/modtools): lists Bonesy's MoPaH as converting GR2 models into D2R-compatible models and replacing embedded textures. This is a candidate to evaluate in the terrain experiment, not evidence of terrain/physics export compatibility or an automation API.
- [Existing format notes](../LEGACY-FORMATS.md) and [gameplay direction](gameplay-direction.md): local verified format boundaries and unresolved warp/terrain questions. The linked Paul Siramy tutorial could not be fetched during this investigation; its content was not newly verified.

Current source and the inspected game JSON provide the capability assessment above. External tool compatibility and new terrain behavior still require the experiments described here.
