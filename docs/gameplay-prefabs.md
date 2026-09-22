# Gameplay prefabs

Use a mod workspace with a separate extracted asset folder. Select standalone HD models before opening **Create → Gameplay prefabs…**, then choose **Save selection as prefab…**. Name the prefab and confirm its HD units per tile. The capture dialog lets you explicitly include additional DS1 units; clicking list rows toggles membership.

Ownership links determine related content. A selected model includes its linked unit and owned collision; an explicitly selected linked unit includes its HD owner. Nearby scenery and units are never inferred to belong to the assembly. Set up ownership in the paired editor before capturing a chest, camp or doorway that needs it. Saving the prefab leaves the source scene and its undo history unchanged.

## Contents

- Complete supported HD entity components, retaining transforms, variations, model/physics fields and unknown fields within those supported components. New instances receive fresh entity IDs and unique names.
- Logical monster/NPC/superunique/object recipes, raw placement flags, relative subtile positions, patrol points and actions.
- Owned collision cells, relative to the same tile anchor, including overlaps among included owners.
- Bundled referenced HD files, available model LODs, model texture references and recursively referenced JSON assets. The manifest records logical paths, physical file paths, sizes and SHA-256 hashes. Matching dependency declarations retain their metadata.

The source anchor is the minimum tile position across included model anchors, unit positions and collision cells. HD height is relative to the first included model. Placement translates the assembly without rotating or rescaling it. Anchor X/Y are tiles; gameplay positions and patrols are stored in subtiles (five per tile).

## Library and placement

Each saved prefab is a new directory under `<workspace>/.rle-prefabs`, containing `prefab.json` and `assets/data/hd/...`. Copy the entire directory to reuse it elsewhere, then choose **Open prefab folder…**. The browser shows HD geometry, static gameplay-unit reference appearances, collision cells and patrol segments. Gameplay-unit appearances come from the target game assets; the bundle does not contain the game's complete monster/object definitions, animation systems or gameplay tables.

Choose an anchor tile and HD height, and confirm the target scene's scale. Placement requires the same act and scale, supported unambiguous gameplay recipes, matching definition rows, valid ground and in-bounds anchors/patrols. Numeric preset IDs may differ: the importer resolves the same logical recipe against the target catalog. It rejects changed definitions and ambiguous mappings rather than silently changing the assembly. Vanilla runtime pack markers such as `place_fallen` remain unsupported; a direct, resolved preset recipe is required.

Missing bundled assets are installed into the workspace under their game-relative paths. Matching existing assets are reused; conflicting assets or dependency declarations block placement. Asset files are shared workspace dependencies and remain after undo. A failed placement rolls back newly installed files, while one scene undo removes the inserted HD entities, DS1 units, patrols, owned collision and JSON dependency additions. Existing blocking and overlapping owners are preserved. **Save Scene** writes the assembled scene and placement links using the existing backup/rollback workflow.

## Supported boundary

This version supports up to 256 standalone HD models and 512 units per prefab. Supported components are transforms, models, model variations, model platform tiers and physics bodies. Parented entities, terrain, other behavior components and ambiguous ownership are rejected. Collision captures owned DS1 overrides; it does not copy floor tiles, DT1 wall geometry or automatically infer collision from meshes.

Door-object placements and their linked visuals/collision can be saved. DS1 exit markers and cross-area Vis/Warp connections are authored separately with the entrance/exit editor. Prefabs do not register areas, compile gameplay tables, remap arbitrary entity references or simulate quest/NPC services.

Dependency discovery covers explicit HD references, decoded model textures, available LOD variants and JSON reference chains. It does not prove dependency closure for every proprietary binary format or engine behavior. Verify spawning, collision, object interaction, patrols and travel in a disposable game test before distributing an authored level.
