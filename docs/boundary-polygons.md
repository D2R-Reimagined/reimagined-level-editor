# Custom boundary outlines

The subsequent [regeneration workflow](boundary-regeneration.md) enables editing
and rebuilding new exports while preserving unrelated content. The schema-3
results below describe this earlier checkpoint.

Create > Build boundary (experimental) now accepts concave outlines made from
horizontal and vertical segments. An L-shaped enclosure is no longer forced
into a rectangle. HD wall pieces, owned DS1 collision and generated outline
tiles follow the same boundary. Source floor graphics and variants are retained.

Enable Custom outline, then enter 4 to 64 tile-centre corners, one `X,Y` pair
per line, or click the preview to append corners. The last corner connects back
to the first automatically. Either winding direction is accepted. Gold marks
walls and cyan marks the opening. The numbered gate segment is one-based in the
dialog; gate start is an offset from that segment's first corner. The gate must
be at least three tiles wide and remain away from both corners.

The preview rejects diagonal, crossing, touching, pinched and out-of-bounds
outlines. Walls, interior and both gate approaches require resolved clear floor.
Invalid drafts disable export but can still be closed without changing the scene.
The folder picker selects a parent for a fresh boundary workspace. Export creates
the paired scene, DS1, collision links, outline DT1 and schema-3 boundary receipt.
The receipt records the corners and gate segment for subsequent tooling.

## Verified result

- Build passed with zero errors and two existing Template-hiding warnings.
- The assertion runner passed 734 checks, including winding, concavity, gate
  placement, invalid geometry, floor validation and owned-collision undo.
- `--boundary-polygon-check` passed 2,950 checks across independent 26-by-24 and
  31-by-27 fixtures using extracted DT1 and wall-model assets. Both exported 53
  wall pieces; source preservation, gate approaches, collision and save/reopen
  ownership passed.
- Actual Windows editor testing created the six-corner outline, appended a corner
  by clicking the preview, rejected and closed an invalid diagonal draft, exported,
  saved and reopened the result. All 53 models reloaded with no missing markers.
  The selected wall retained one owned collision tile. Ground / gameplay showed
  the concave collision boundary and open gate with zero unresolved floor cells.
- Evidence is retained in `artifacts/polygon-boundary/ui-test`. These fixtures
  contain gameplay floor and HD walls, without an HD terrain mesh.

The dialog also now uses the editor's dark colours and an accurate boundary
folder-picker title. The previous rectangular exporter remains available.

## Remaining boundaries

Only a single orthogonal loop and one gate are supported: no diagonal segments,
curves, holes or multiple gates. Collision remains whole-tile, with at most 256
wall pieces. Grid calibration is explicit and wall base height is the zero plane.
The affected floor must use one resolved layer with invariant collision variants;
one free DT1 slot and enough unused floor styles are required. Broad contour masks
cannot represent narrow one-tile passages. Existing generated boundaries cannot
yet be regenerated safely in place.

This does not generate or sculpt HD terrain, repair the reported cave ground gap,
or register a new native area, runtime tileset slot or automap group. Use the portable registration command for these receipts. Native gameplay, multiplayer
and cancellation during an active export remain unqualified for this version.
The earlier game-tested schema-1 cave is a separate frozen candidate.
