# Experimental cave authoring

The Create menu adds paired cave ground growth and generated boundaries. Boundaries
support one horizontal/vertical outline, including concave shapes, one gate and linked
full-tile collision. Reopening a generated boundary restores its settings; regeneration
replaces its recorded content in a fresh workspace and rejects conflicting manual edits.
Unrelated content is preserved.

1. Open a paired project and calibrate its grid.
2. Use **Extend ground (experimental)** for supported Act I cave terrain, or
   **Build boundary (experimental)** for stock walls, collision and contour tiles.
3. Save and reopen the export. General boundary exports retain existing terrain;
   they do not create an HD ground mesh for an empty project.
4. Prepare independent Act I cave registration against complete baseline tables:
   `node scripts/PrepareBoundaryArea.cjs <baseline-data> <boundary-export> <fresh-output>`.
5. Open both registered areas. In **Entrances and exits**, assign a compatible existing
   definition to an unused slot, place its marker and save. Preview/apply a reciprocal
   connection and save again. Test travel and landing in game before distributing.

Registration derives dense IDs, dimensions, placement, DT1 slots and automap rows.
It preserves unrelated table bytes using a bundled TSV helper. Donor labels are reused. The command does not install or launch anything.

## Validation

Run the custom .NET assertion runner and the registration suites:

```text
dotnet run --project tests/D2RLevel.Tests
node --test scripts/PrepareCaveArea.test.cjs scripts/PrepareBoundaryArea.test.cjs scripts/tsv.test.cjs
```

Recorded editor checks cover boundary export, save/reopen, regeneration, Warp assignment
and reciprocal route persistence. Earlier disposable-profile fixtures confirmed added-ground
walking, return travel and Save and Exit/reload; a subsequent boundary fixture confirmed
visible automap and blocking walls. These results do not qualify the latest generalized
exports or the new area-to-area route.

## Limits

Act I cave registration; orthogonal boundaries; one gate; explicit grid calibration;
zero-height walls and conservative full-tile blocking. Arbitrary DT1 art, terrain
sculpting, new Warp artwork, automatic scenery collision and multiplayer are unsupported
or unqualified. The earlier triangular terrain gap and rough wall joins remain open.

Details: [ground export](cave-ground-extension.md), [outlines](boundary-polygons.md),
[regeneration](boundary-regeneration.md), [registration](boundary-area-registration.md),
[connections](entrances-and-exits.md).
