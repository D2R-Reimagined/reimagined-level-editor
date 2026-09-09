# Validation

## Automated checks

```powershell
dotnet build D2RLevelEditor.slnx
dotnet run --project tests/D2RLevel.Tests
```

The dependency-free assertion runner uses synthetic fixtures and does not require game assets or Granny. Coverage includes JSON preservation, DS1 versions 16–18, DT1 decoding, collision ownership/overlap, gameplay units and patrols, shared undo/redo, paired saves, rollback, external-change detection and deletion. It returns a nonzero exit code on failure. `dotnet test` does not run it.

## Optional checks with extracted assets

```powershell
dotnet run --project tests/D2RLevel.Tests -- --audit 'C:\maps\town.json' 'C:\extracted\data' 'missing-assets.txt'
dotnet run --project tests/D2RLevel.Tests -- --probe 'C:\assets\prop.model' 'C:\tools\granny2.dll'
dotnet run --project tests/D2RLevel.Tests -- --texture 'C:\assets\prop_alb.texture'
dotnet run --project tests/D2RLevel.Tests -- --legacy 'C:\maps\town.ds1' 'C:\extracted\data'
dotnet run --project src/D2RLevel.App -- --preset 'C:\maps\town.json' --data 'C:\extracted\data' --granny 'C:\tools\granny2.dll' --smoke-output 'C:\scratch\editor-check'
```

Use a new output directory for each desktop smoke. Append `--features` for model explorer and DS1 interactions, or `--workspace-smoke --delete-smoke` for workspace discovery, saves, deletion and undo. Workspace checks require a preset and matching DS1 under their original game-relative paths; they copy maps beneath the output directory. Generated screenshots, settings and maps belong outside source control.

## Verification boundaries

Local checks have exercised 205 assertions and published Windows builds with paired saving, linked collision, deletion and restoration of model geometry. Portable ZIP extraction and execution have also been checked locally. Historical screenshots and logs are local artifacts, not distributed repository files.

These checks do not establish compatibility on every machine, large-map performance or live-game correctness. Use a separate test mod and disposable character. Keep original game-relative paths and filenames; a different randomly selected town variant will not display your edits. Verify loading, placement, collision and units in-game. Terrain rendering is approximate and DT1/wall collision remains independent of owned floor overrides.
