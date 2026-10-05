# Boundary authoring

Open a saved paired project with resolved floor and explicit grid calibration, then
choose Create > Build boundary (experimental). Choose a wall model, rectangle or
custom orthogonal outline, and one gate at least three tiles wide. Export to a fresh
workspace, inspect the linked blocking, Save Scene and reopen.

Concave outlines are supported. Generated wall pieces own full-tile DS1 blocking;
contour DT1 output preserves each source floor's graphics, variants and collision.
Regeneration restores the recorded settings and replaces only generated content,
preserving unrelated objects. Edited generated content is rejected as a conflict.

The exporter uses the opened map's dimensions, act and tileset; it is not bound to
one cave fixture. Tests include mixed-floor Act IV data and independent map sizes.
The default wall model is stock cave art and can be replaced by an explicit model path.
This does not generate HD ground, register an area or simulate native game physics.

Only one orthogonal loop and one gate are supported. Collision is whole-tile, wall
base height is zero, and at most 256 wall pieces are allowed. Manual grid calibration,
resolved invariant floor collision and spare tileset/style capacity are required.

For connections, use Create > Entrances and exits. Assign an existing compatible
hidden Warp definition to an unused fixed-area slot, place its marker and save before
previewing/applying a reciprocal route. Existing definitions and connections are
preserved. Arbitrary Warp editing and new exit art are unsupported.

Run `dotnet run --project tests/D2RLevel.Tests` for offline checks. Asset-backed export
and regeneration checks use `--boundary-regeneration-check <asset-data> <decoder> <fresh-output>`.
Editor export, regeneration and connection save/reopen were previously exercised.
Latest generalized routes and exports still require native gameplay qualification.
