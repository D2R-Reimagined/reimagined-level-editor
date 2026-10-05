# Offline area registration for portable boundary exports

`PrepareBoundaryArea.cjs` prepares table registration for boundary receipt schemas
2, 3 and 4. It reads the candidate's project, DS1 dimensions, DT1 identities and
the selected baseline tables. Level and preset IDs, world placement, tileset
slots, mask and contour styles are no longer fixed to the original cave fixture.

```text
node scripts/PrepareBoundaryArea.cjs <baseline-data> <boundary-export> <fresh-output>
node --test scripts/PrepareBoundaryArea.test.cjs
```

The baseline data directory must contain complete `global/excel/levels.txt`,
`lvlprest.txt`, `lvltypes.txt` and `automap.txt`. The export directory contains
`boundary.json` and one project below `data/`. Output is a new offline workspace,
not an installation. The command does not read a running game or deploy files.
This is a preparation command; it is not yet a button in the editor.

## Allocation and preservation

- Dense physical level and preset indices determine the next IDs. Sparse IDs,
  ambiguous rows and a DS1 path already registered in the baseline are rejected.
- The initial supported art family is Act I caves, using native level type 3 and
  its contiguous `1 Cave` automap group. A fixed cave donor supplies ordinary
  level settings and labels; quest, population and connection fields are cleared.
- Dimensions come from the DS1. Placement is beyond the existing Act I extents,
  checked against all three difficulty rectangles and the supported coordinate range.
- Existing matching DT1 slots are reused. New files require empty slots excluded
  by every existing preset mask, conservatively including other level types.
  The 32nd bit is handled as an unsigned mask. This may reject a baseline with no
  globally unused slot even where a narrower analysis could prove one usable.
- Generated DT1 floor identities must match the receipt. Matching contour art is
  reused; conflicts or ambiguous sequence ranges are rejected. New rows stay in
  the native cave group. Mask zero explicitly has no automap cell.
- Removing the planned table additions and restoring assigned slots must recreate
  the original serialized tables exactly. CRLF, headers and unrelated cells are
  preserved using the bundled byte-preserving TSV helper. Different baseline assets cannot
  be overwritten by the candidate. Input hashes are rechecked before publication.

The output includes the candidate data, four prepared tables, updated editor
tileset context and `registration.json` with allocations and input hashes. Keep
this receipt for any later deployment review. Allocation is valid only against
that exact baseline: separately prepared packages must not be merged blindly.
After regenerating geometry, rerun registration against the intended baseline.

## Evidence and open work

Sixteen targeted tests pass. Older area/workflow/package tests report 35 passed
and two existing asset-dependent skips. The real-data probe uses the frozen
game-tested cave tables and the regenerated 26-by-24 authoring fixture. It assigns
level 149, preset 1097, slots 1 and 32, mask 2147483649 and origin 64,2960. Eight
existing contour rows are reused and one mask-zero row is added. These numbers
are test results, not constants in the implementation.

The prepared output was opened, saved and reopened in the Windows editor:
52 models loaded, scene and link data were preserved, and Ground / gameplay
reported the updated unsigned mask with zero unresolved floor cells. This UI
check reused the unchanged application build from the regeneration checkpoint.

This removes fixed-ID registration preparation. It does not establish native
entry, automap rendering, persistence or multiplayer. Entrances/exits remain
unauthored; donor labels are reused; custom automap atlases and other act/art
families are unqualified. The fixture has no HD terrain mesh. It is not a complete
playable area, and no runtime deployment or game launch was performed.
