# Regenerating an authored boundary

New exports retain a `.rle-boundary.json` record beside the preset. Reopen that
preset, choose Create > Build boundary (experimental), change its corners, gate
or wall settings, and choose Regenerate boundary. The dialog restores the saved
settings and previews against the floor with the previous boundary removed.

Regeneration exports to a fresh workspace, preserving the input. It removes only
the recorded generated models and their collision contributions, restores the
recorded floor identities, then builds the replacement walls and outline tiles.
The generated DT1 occupies the same logical slot; repeated rebuilding does not
accumulate outline files or progressively clone generated floor identities.
Unrelated objects and their collision links remain part of the output.

The record contains exact generated object data, link ownership, changed floor
cells and the generated tileset hash. Edited or missing generated walls, changed
outline floors, broken links, or a changed generated tileset stop regeneration.
Conflicts are reported rather than silently discarding manual work. Whole-tile
collision ownership retains its existing shared-owner and protected-blocking
rules. A remaining obstacle may make the requested new boundary invalid.

Exports now use boundary receipt schema 4. Earlier exports without the revision
record cannot be regenerated: export their original source with this editor
first. The record travels with the data folder and survives Save Scene. Keep it
with the preset; it is not a replacement for the normal collision-link sidecar.

## Validation

The normal assertion runner passes 734 checks. The optional integration probe is:

```text
dotnet run --project tests/D2RLevel.Tests -- --boundary-regeneration-check <asset-data> <granny2.dll> <fresh-output>
```

It passes 2,950 export checks and 3,505 regeneration checks across 26-by-24 and
31-by-27 fixtures. Each fixture rebuilds three times with changed corners and a
moved gate. Every interior collision cell is checked for stale or missing
blocking. An unrelated HD object and owned footprint survive. Generated wall
and floor edits are rejected without publishing output or modifying the source.

The Windows editor workflow was also exercised: reopen a boundary, edit its
concave corner and gate, regenerate, Save Scene, and reopen the new output.
The result contains 51 replacement walls and one preserved unrelated object.
Evidence is under `artifacts/boundary-regeneration/ui-test`.

This is editor/export qualification. HD terrain generation, automatic native area
and automap registration, native gameplay and multiplayer remain separate work.
Regeneration supports the existing single orthogonal loop and one gate, not
curves, diagonal walls or holes. It does not merge manual changes to generated
content or overwrite a live workspace in place.
