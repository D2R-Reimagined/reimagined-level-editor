# Experimental cave ground extension

Open a saved paired Act I cave, then choose **Create > Extend ground (experimental)**.
Select the region and donor floor tile, set the height and optional growth, and export
to a fresh folder. Inspect the paired result, Save Scene and reopen before deployment.

The supported terrain is rigid and single-mesh, with an identity scene transform and
10 HD units per tile. Growth adds at most 16 logical tiles per axis in positive X/Y,
retaining the final DS1 border and existing gameplay data. HD terrain, LODs, physics
and model-catalog dependencies are exported together. Inputs are hash-checked before
publication; unsupported partial physics or transforms reject the export.

This is a floor operation. Use boundary authoring for enclosing walls. It is not
arbitrary terrain sculpting or biome rendering. Earlier cave gameplay confirmed
added-ground traversal, return travel and Save and Exit/reload; a triangular ground
gap remains unresolved. Generalized candidates require their own game qualification.

See [experimental cave authoring](experimental-terrain.md) for the combined workflow.
