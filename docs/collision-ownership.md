# Moving collision with HD objects

The editor now supports a unit and a collision footprint on the same HD entity. Both translate in one history transaction, along with the unit's patrol. Existing version-1 sidecars already represent both fields, so earlier unit-only and footprint-only files remain readable.

## Ownership rule

For each owned floor cell, the exported DS1 override is:

`protected original override OR at least one current footprint owner`

A move computes the old and new unions before writing. Cells still occupied by another owner stay blocked. Cells the last owner leaves return to their recorded original override. Newly occupied cells capture their current override before the editor adds blocking. Returning to a previously visited cell recaptures its current baseline, preserving intervening manual edits. Only occupied cells need retained baselines; history holds earlier snapshots for undo.

This protects three distinct cases:

| Situation | Result when the selected model leaves |
| --- | --- |
| Another linked model owns the same cell | Blocking stays until the last owner leaves. |
| Existing floor override was not explicitly claimed | That override stays, even after every owner leaves. |
| Blocking comes from a wall, another floor layer, or a DT1 | That data is not changed by linked footprint movement. |

The DS1 floor flag is one bit, with no model-owner identities. The HD model bounds likewise do not establish which existing gameplay collision belongs to that model. It is therefore unsafe to infer ownership and erase original blocking automatically. **Claim existing floor overrides** is an explicit choice when initially assigning new footprint cells. It defaults off and resets between drafts. It does not transfer another linked owner's cells or rewrite an existing shared baseline. Link every overlapping model whose blocking should move; leave ambiguous background blocking protected.

## Editing workflow

1. Select the HD model and choose **Add collision…**. An existing unit link stays attached. A render-bounds suggestion opens as an editable draft; **Suggest model footprint** can regenerate it.
2. With **Draw footprint** enabled, left-drag to add or erase cells. Starting on a selected cell erases; starting elsewhere adds. Pointer movement interpolates between cells. Zoom keeps the draft; Escape, dismissal, changing selection, or deactivation discards it without writing map data.
3. Review cyan cells and magenta cells shared with other models. The status counts other owners and protected floor overrides. Bounds include overhangs, so trim the suggestion as needed.
4. **Link footprint to HD** / **Save collision link** commits a single undoable edit. Subsequent HD movement updates unit/patrol coordinates and the collision footprint together on release.
5. **Edit collision…** reopens the stored footprint at its current position. Removing draft cells releases only this object's contribution. **Remove collision link** removes the complete footprint while retaining its gameplay unit link. **Unlink** remains different: it leaves current map data in place and stops synchronized movement.
6. **Save linked pair…** exports both documents and the local ownership sidecar. Reopening the exported JSON restores ownership, including overlaps and baselines.

Unit coordinates round to whole subtiles; floor overrides round to whole tiles, using the original anchor to avoid accumulating rounding drift. The default is 10 HD units per tile, with configurable calibration. A combined link must use one common scale. Rotating/scaling still requires relinking; this work does not generate custom DT1 collision or move wall graphics, warps, terrain, or unrelated units.

## Verification

The console suite includes 300 deterministic randomized moves with overlap and interleaved cross-view undo/redo. Each resulting floor cell is compared against an independent expected union of the two model positions and a static baseline. Focused checks cover both attachment orders (unit then collision and collision then unit), attaching after movement, reshaping moved footprints, removing only collision, unchanged drafts, scale mismatch, paired reload, and original-byte preservation.

Desktop smoke additionally exercises a real map's draft add/erase, zoom preservation, commit, undo/redo, removal retaining the unit link, paired export and reload. These checks establish editor/data behavior; game loading and walkability of combined exports still need live-game verification.
