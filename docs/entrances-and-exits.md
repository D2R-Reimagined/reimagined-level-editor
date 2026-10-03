# Entrances and exits

Open **Create → Entrances and exits…**, or use the same button in the paired DS1 window. Choose the area context when several preset rows reference the current map. The editor resolves `lvlprest.txt`, `levels.txt` and `lvlwarp.txt` using workspace overrides before extracted base files.

The left list shows all eight outgoing slots. Select a row or numbered map marker to see its destination, return slots, incoming area connections, Warp directions, landing/exit-walk offsets and selection rectangle. The destination picker filters by name or ID and lets you inspect every advertised fixed-map variant. Generated areas remain inspectable through their table links; their runtime geometry is not inferred from a reusable room.

## Warp overlay

Hover a numbered exit on either map, or select one, to draw its `lvlwarp` row around the exit. The row is the slot's `Warp#`, taking the definition for the exit's direction first and then the `b` (both-ways) one.

* **Dashed gold**: the selection box the mouse highlights (`SelectX`, `SelectY`, `SelectDX`, `SelectDY`). These are screen pixels from the warp's base, projected onto the floor using the classic 32 × 16 pixel subtile, so the box leans the way the isometric screen does. It is not drawn for `NoInteract` warps or a zero-sized box.
* **Green**: where a player arriving through the warp appears (`OffsetX`, `OffsetY`, in subtiles from the base). **Blue**: where they then walk to (`ExitWalkX`, `ExitWalkY`, added to the arrival point).
* The card names the destination, the warp's name, Id and direction, the box, and whether the exit lights up on hover (`LitVersion`, `Tiles`) or cannot be clicked.

The base is the corner of the group's scan anchor tile (a hidden tile, or sub-index 0 or 4). This placement follows the table definitions and the classic projection; confirm it in game before shipping new numbers.

## Move a hidden exit

Select a numbered exit, choose **Move selected exit**, and click ground on the current map. Escape cancels. An ordinary click on empty map space clears selection without moving anything. While the move is armed, the group follows the pointer with its warp overlay. It is green where it can go; where it cannot, it is red and the card gives the reason. The same checks guard the move itself.

Only small, contiguous hidden groups on one wall layer and direction can move. The full group retains its slot, tile flags and orientation bytes. Bounds, occupied destination cells, missing floors, DS1 blocking overrides and owned footprints are checked before any bytes change. Visible doorways, separate groups sharing one slot, and special placement markers remain inspectable. This does not relocate HD artwork or validate destination landing offsets against full game collision.

Moves participate in the paired scene's shared undo, including its placement-link fingerprint. Use **Save Scene** in a workspace or **Save linked pair** for a manually opened pair.

## Add an exit and find a place for it

A new level starts with no exits, and many maps leave some of their eight slots unused. Select a slot in the left list (its row shows the `Vis` destination), then:

* **Place new exit** arms placement. A one-tile hidden exit follows the pointer with its warp overlay, green where it can go and red with the reason where it cannot. Click to add it. It uses vanilla's single-tile hidden exit encoding: the hidden flag, the slot as main index, sub-index 0 and property byte `0x81`, on wall layer 1. It faces `l` (orientation 10) unless the slot's warp is defined for `r` only.
* **Suggest spots** ranks places for the selected exit and numbers the best five on the map. It works both for a slot with no tiles and for a hidden group that could move. Hover a marker to see the spot and its warp, and click it to add or move the exit there.

Every suggestion passes the same checks as adding or moving an exit, and the warp's arrival and walk-to points must land on walkable subtiles. Walkability uses the DT1 collision of the tiles there, DS1 blocking overrides and owned footprints. Suggestions are then ranked by:

* how much walkable ground surrounds the arrival point;
* distance from the slot's other exits;
* distance from the map edge;
* the side chosen in **Prefer**, when one is set.

The ranking also spreads suggestions apart. `levels.txt` offsets do not reliably say where a destination lies, since most are filled in at runtime, so the side preference is yours to choose. Additions and moves share the scene's undo. Confirm the landing and the route both ways in game.

## Author area connections

Open a mod workspace with a separate extracted asset folder. Select a source slot, choose another area, and select an exit on its map. **Preview connection change** displays every affected route before **Apply preview** stages one undoable edit.

- Two unused endpoints become one reciprocal pair.
- Two separate reciprocal pairs across four areas can exchange partners. For example, A↔X and B↔Y become A↔B and X↔Y. The former return routes are included in the preview.
- All affected areas must be in the same act, use fixed presets (`DrlgType 2`), enable exit scanning (`Scan 1`), and have the selected marker and an unambiguous direction-compatible Warp definition in every advertised variant. Missing/ambiguous data and existing one-way links block authoring.

Only the affected numeric `Vis` cells change. Existing `Warp` definitions, map geometry, other table bytes, BOM and line endings are preserved. **Save Scene** writes the workspace `global/excel/levels.txt` override together with JSON, DS1 and link metadata, retaining `.bak` files for replaced files and rolling back earlier replacements if a later write fails. A marker-only save does not create a Levels override. External table/dependency changes block a stale connection save.

This editor adds hidden exits but does not create visible doorway exits or new exit artwork, arbitrary portal behavior, new area registrations, or generated-level links. It does not update `levels.bin`; use the mod's normal text-table compilation workflow. Confirm loading, travel in both directions and landing positions in a disposable game test before shipping.

## Mapping evidence

Ordinary exit tiles use orientations **10/11** and main indices **0–7**. Main indices 8–29 carry pop metadata; 30–33 are special placement markers and do not map to `Vis0–3`. Directional `lvlwarp` rows can legitimately share an Id.

The mapping was cross-checked against extracted D2R maps, including Arreat Summit's hidden slot 0/1 markers, and the reference engine's [preset scanning](https://github.com/ThePhrozenKeep/D2MOO/blob/master/source/D2Common/src/Drlg/DrlgPreset.cpp) and [exit tile/warp creation](https://github.com/ThePhrozenKeep/D2MOO/blob/master/source/D2Common/src/Drlg/DrlgRoomTile.cpp). These references and local round-trip checks do not establish current D2R runtime compatibility.
