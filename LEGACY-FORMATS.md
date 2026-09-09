# Legacy floors and collision editing

The legacy ground is now read from DS1 floor layers and resolved against DT1 files. The HD scene's terrain mesh and biome/material system are separate. Moving an HD entity does not update legacy graphics or collision. This implementation deliberately makes no claim that the two views are spatially aligned.

## References consulted

- [Paul Siramy's editor manual](http://paul.siramy.free.fr/_divers/ds1/doc/index.html): floor layers, wall layers, shadows, walkability display, special tiles, objects and paths.
- [Paul's tutorial 1](http://paul.siramy.free.fr/_divers/ds1/doc/tut01/index.html): LvlTypes tile files, LvlPrest Dt1Mask, town variants, warps and roof groups.
- [Paul's downloads](http://paul.siramy.free.fr/_divers/ds1/dl_ds1edit.html): the 2011-10-30 source archive was consulted for binary field layouts and tile-block encoding. Its source was not copied into this project.

The manual and tutorial describe why tiles cannot be interpreted from a DS1 number or filename alone. The mapping follows:

1. Match the DS1's relative `global/tiles` path to exactly one LvlPrest File1–6 entry.
2. Use its LevelId to find Levels, then LevelType to find LvlTypes.
3. Enable LvlTypes File 1–32 according to the unsigned 32-bit LvlPrest Dt1Mask.
4. Resolve each nonempty floor cell by orientation 0, main index and sub index in those DT1s.
5. Draw the indexed tile graphics using the act palette.

Missing table context, missing DT1s, unsupported formats and truncated files produce errors rather than guessing a tileset. Unresolved tile keys appear magenta and are counted. Duplicate tile keys retain all variants; the first file/header variant is shown deterministically, without claiming game-seeded variation or animation.

## Implemented binary subset

- DS1 versions 16–18: stored width/height plus one; act plus one; bounded filename strings; interleaved wall/orientation pairs and one or two floor layers. Empty cells have a zero low byte. Main index is bits 20–25; sub index is bits 8–15. Collision painting patches only the Unwalkable bit (bit 17, mask `0x00020000`, `prop3 & 0x02`). Gameplay movement patches existing unit coordinates and associated patrol anchor/point coordinates. All other bytes remain verbatim, including IDs, flags, actions and group records. See [gameplay direction](docs/gameplay-direction.md).
- DT1 version 7.6: tile count/header offset at 268/272; 96-byte tile headers; orientation/main/sub at 20/24/28; block offset/size/count at 72/76/80. Floor graphics occupy 160 × 80 pixels. Blocks use 20-byte headers with tile-relative data offsets. Format 1 is a 256-byte isometric diamond; formats 0x1001 and 0x2005 use skip/run scanlines. Every extent and run is checked.
- Act `pal.dat`: 256 BGR entries, palette index zero transparent.
- DT1 collision: 25 bytes at tile-header offset 40, stored bottom-to-top and mapped to a 5×5 DS1 subtile grid. Floor and wall contributions combine, including both parts of orientation-3/4 corners and the documented orientation-18/19 wall-end fallback. Flags `0x01` and `0x08` indicate movement blocking in this preview. A missing floor or DS1 override fills the whole tile. Variant collision is conservatively combined and marked as variant-dependent; unresolved tile contributions are marked unknown.

## Collision controls and export

The DS1 Unwalkable flag is the mechanism described under Advanced Tile Editing in Paul's manual and `misc_search_walk_infos` in his source. Painting sets it on the first nonempty floor layer; floorless cells are skipped. Clearing removes the override from all floor/wall layers at the chosen location. Clearing is not a force-walkable operation: DT1 collision remains. No shared DT1 file is modified.

Each drag is a preview until release, then one undo command. Escape, capture loss and window deactivation cancel it. Save writes a separate DS1, reparses/verifies it, and keeps `.bak` when replacing an existing output. The source cannot be overwritten. Collision-only copies can be reopened using the original DS1's tileset context; other changed bytes are rejected in that flow.

For game testing, put the exported DS1 at the original `data/global/tiles/.../*.ds1` path and name in a separate test mod. An edited HD JSON goes separately under its corresponding `data/hd/env/preset/.../*.json` path. Neither filename changes nor an editor-only overlay affect gameplay. The game must load the matching town variant.

The editor supports presets with a unique table match and a fixed nonzero LevelId. Procedural presets with LevelId zero need explicit level context before this can be generalized. DS1 v1–15 and other DT1 versions are rejected explicitly.

## Boundaries for the next stage

This is static tile collision editing, not full game collision simulation. Dynamic objects/doors, warp links, room generation, tile randomization and game-specific collision masks can affect actual movement. Special tiles without a resolved DT1 contribution stay unknown. HD terrain shading and coordinate registration still need dedicated work, and moving an HD prop does not automatically move legacy tiles. Fine-grained DT1 flag editing and removal of DT1-derived blocking are not implemented.

## Local evidence

Stock `act1/town/townn1.ds1`: v18, 57 × 41 cells, one floor layer, Dt1Mask 959, nine enabled DT1 files and zero unresolved nonempty floor cells. Synthetic checks cover two floor layers, layer order, index extraction, empty-cell semantics, diamond/RLE decoding and truncation rejection. The feature smoke renders both windows using extracted assets; see `VALIDATION.md`.
