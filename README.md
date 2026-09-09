# Reimagined Level Editor

A Windows level editor for Diablo II: Resurrected, built with C# and .NET 10. Edit HD preset JSON and DS1 gameplay data together through a WPF 3D viewport and collision tools.

## Features

- Workspace scene browser with paired JSON/DS1 discovery, search and recent workspaces.
- Textured model preview, model explorer, placement, transforms and deletion.
- Terrain toggle, inline collision preview and a separate interactive DS1 window.
- Explicit links between HD models, DS1 units and collision footprints.
- Shared undo/redo and collision ownership that preserves overlapping blocking.
- Save Scene for JSON, DS1 and link metadata, with backups and external-change detection.
- Save notifications, remembered asset settings and window placement.

## Requirements

- Windows x64; .NET 10 SDK when building from source.
- Your own extracted D2R assets and mod data. Game files are not included. Use extracted loose files, not the installed game's CASC archive directory.
- The bundled 64-bit `granny2.dll` provides HD mesh decoding. It is copied from `src/D2RLevel.App/Native/` to `Native/` beside the EXE. Keep that folder with the application. See the runtime provenance notice below.

## Build and run

Initialize the pinned LSLib submodule after cloning:

```powershell
git submodule update --init --recursive
dotnet build D2RLevelEditor.slnx
dotnet run --project src/D2RLevel.App
```

Optional inputs:

```powershell
dotnet run --project src/D2RLevel.App -- --preset 'C:\maps\town.json' --data 'C:\extracted\data' --granny 'C:\tools\granny2.dll'
```

`scripts/Start-Editor.ps1` accepts `-Preset`, `-DataRoot`, `-GrannyPath` and `-DotNet`. It starts `artifacts/app/D2RLevel.App.exe` if available, otherwise runs the source project.

## Workspace workflow

1. Choose **Asset folder** for extracted models, textures and DT1 files. This can be outside your mod.
2. Choose **Load Workspace** and select the mod's `data` directory. Scenes require both `hd/env/preset/<relative>.json` and `global/tiles/<relative>.ds1`, or a valid local sidecar mapping. Missing maps are not copied from the asset folder.
3. Select a scene on the left to load both files. The last workspace restores on startup.
4. **Save Scene** or **Ctrl+S** writes both maps and `.rle-links.json` metadata back to their workspace locations. Previous files receive `.bak` backups. Files changed externally since opening prevent a save until reopened.
5. **Workspace Explorer** returns to the list and prompts before discarding edits. Manually opened presets retain the separate export-copy workflow.

Saves stage and validate files before replacement. A failed replacement attempts to restore earlier files; the three-file operation is not atomic against crashes or power loss. Keep backups and validate edits in-game.

## Controls

| Action | Control |
| --- | --- |
| Look around | Right-drag |
| Move forward/backward | Mouse wheel |
| Pan | Middle-drag |
| Orbit selection | Alt + right-drag |
| Faster / finer travel | Shift / Ctrl |
| Move model | Left-drag |
| Cancel drag | Esc |
| Frame selection / whole scene | F / Home |
| Delete model | Delete in viewport/entity list, or Inspector button |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Save | Ctrl+S |

The DS1 viewer also supports middle-drag panning. Home restores the HD camera's default orientation matching DS1 axes. Transform text must be applied before it becomes a document edit.

## Link models to gameplay

Select a model and **Link in DS1…**. Choose the intended gameplay unit and **Link unit to HD**, or use **Suggest model footprint**, review the cells, then **Link footprint to HD**. A unit link alone does not give a model floor collision. **Add collision…** and **Edit collision…** let you draw and refine its footprint.

Unit links preserve the original offset. **Align linked unit to HD** explicitly aligns the unit and patrol to the model origin. The default calibration is 10 HD units per tile; verify it for your scene. Linked translations move units in whole subtiles and footprints in whole tiles. Review and relink after rotation or scale changes.

**Claim existing floor overrides** defaults off: pre-existing blocking remains unless you explicitly assign ownership. Overlapping footprints stay blocked while any owner occupies them. **Unlink** retains current map data and stops synchronized movement. **Delete object** removes a standalone model, its linked unit and owned collision as one undoable edit. Shared blocking and shared patrol anchors remain. Terrain and parented entities cannot be deleted through this action.

Save Scene persists workspace links. **Save linked pair…** exports both maps and their sidecar for manually opened presets. Keep the sidecar beside its JSON; relative paths allow moving the pair together. It is editor metadata, not a game asset. Missing or stale links prevent silent HD-only edits. See [collision ownership](docs/collision-ownership.md).

## Current limitations

- Model appearance cannot establish gameplay ownership automatically. HD visuals and DS1 gameplay are separate data.
- Static meshes and albedo textures are previewed. Full game shaders, animation, particles, water and physics are not simulated. Variations preview their first model.
- Terrain uses existing meshes; untextured terrain can use projected DS1/DT1 floor graphics as an approximate reference. This is not a terrain asset writer or the game's biome shader.
- Collision tools edit supported DS1 overrides. Clearing an override cannot remove DT1/wall blocking. Variant-dependent and unresolved cells are indicated separately.
- DS1 editing supports versions 16–18. Unsupported gameplay layouts remain preserved but cannot be edited. Unit IDs are displayed as raw IDs.
- Parent transforms, new gameplay-unit insertion, warp/vis editing, DT1 writing, arbitrary per-subtile painting and new-level authoring are outside the current scope.
- Native mesh decoding must finish before cancellation takes effect. Large scenes use reduced detail and batching; performance varies with assets and hardware.

Use **Missing assets…** to identify files to extract. [Format notes](LEGACY-FORMATS.md) describe DS1/DT1 handling and references.

## Portable package

```powershell
.\scripts\Publish-Portable.ps1
# SDK not on PATH:
.\scripts\Publish-Portable.ps1 -DotNet 'C:\tools\dotnet\dotnet.exe'
```

Creates a timestamped ZIP and SHA-256 checksum under `artifacts/releases/`. Extract the whole ZIP and run `D2RLevel.App.exe`; no separate .NET installation is needed. The EXE bundles managed dependencies and the runtime, with native runtime extraction on launch. Packages are unsigned.

Granny is included by default. Use `-IncludeGrannyRuntime:$false` to produce a package without it. See [runtime provenance](src/D2RLevel.App/Native/NOTICE.txt) for its source, hash and unresolved redistribution status. Packages contain no game assets or personal settings.

## GitHub releases

After the workflows are pushed to `master`, open **Actions → Release → Run workflow**, select `master`, and enter a version such as `1.0.0` or `1.1.0-beta.1`. An optional leading `v` is accepted. Build metadata is not accepted; Windows version components must be no greater than 65534.

The workflow builds and tests on Windows, packages the self-contained editor with Granny, embeds the supplied version, generates semantic release notes, and uploads the ZIP and SHA-256 checksum. It tags the exact tested commit as `v<version>` and marks prerelease versions appropriately. Version selection is manual; commit types categorize notes rather than automatically choosing a version. Source project files are not modified and no version-bump commit is created.

Release notes group `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `style`, `chore` and `revert` commits, highlight `!` / `BREAKING CHANGE:` markers, and retain other commits in Other Changes. Like the launcher, **PR Semantic Commits** requires at least one semantic commit in each PR to `master`, for example `feat(editor): add scene search` or `fix(collision): preserve shared blocking`. Merge commits are excluded from this check. Enable that status check in branch rules if it should block merges.

The release uses the built-in `GITHUB_TOKEN` with `contents: write`; no launcher GitHub App secrets are required. Repository rules must permit it to create release tags. Runs are serialized and existing tags are rejected, not overwritten. If upload fails after tagging, inspect the tag and any draft release and recover that release manually; rerunning with the same version intentionally fails. GitHub CLI uploads assets before publishing the release ([CLI documentation](https://cli.github.com/manual/gh_release_create)).

Local automation checks: `./.github/scripts/Test-ReleaseAutomation.ps1`. To build a versioned package without releasing it, use `./scripts/Publish-Portable.ps1 -Version 1.0.0`.

## Validation and feedback

```powershell
dotnet run --project tests/D2RLevel.Tests
```

The tests use synthetic fixtures and return a nonzero exit code on failure; `dotnet test` does not execute this assertion runner. See [validation](VALIDATION.md) for optional asset-backed checks.

Bug reports should include the scene name, reproduction steps, expected/actual behavior, error text, Windows version and GPU. Do not upload game assets, private mod files, local settings or proprietary DLLs.

## Dependencies and provenance

- [LSLib](https://github.com/Norbyte/lslib), pinned submodule; MIT license at `vendor/lslib/LICENSE`. The adapter builds selected Granny sources without modifying upstream.
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET), texture decoding via NuGet.
- [Texture format reference](https://github.com/CucFlavius/Zee-010-Templates/blob/main/DiabloIIResurrected_Texture.bt), consulted as a format reference.
- [Unity D2R Scene Editor](https://github.com/pairofdocs/Unity-D2R-Scene-Editor), workflow/coordinate reference; no AGPL source copied.
- [Application icon provenance](src/D2RLevel.App/Assets/ICON-PROVENANCE.md).

Third-party licenses apply to their respective components. A license for this project's own source has not yet been selected. Publishing source does not grant rights to redistribute Blizzard assets or the proprietary Granny runtime.

### DS1 NPC previews

The **NPCs** checkbox shows DS1 monster/NPC records in the HD viewport. Town NPCs resolve through the act-specific `monpreset.txt`, `monstats.txt`, and HD character definitions, with workspace overrides before the asset folder. Search names such as `akara` in the entity list, select a preview, and drag it to move its DS1 position and patrol. Selection follows between the DS1 window and HD view. Undo/redo is shared; use **Save Scene** or **Save linked pair** to save gameplay changes.

These are transient static reference poses, never extra entities in the exported HD JSON. Terrain geometry supplies preview height where available; the HD units/tile setting controls horizontal placement. Animation, game character shaders, superunique/spawn-group resolution, and NPC creation/deletion are not implemented. Missing or unsupported character assets retain selectable named markers. A unit already linked to an HD prop must be moved through that prop so its existing collision link remains intact.

Developer check with extracted Act 1 town assets: launch with `--preset <towns1.json> --data <asset-root> --npc-smoke --smoke-output <output-folder>`. The smoke verifies a real Akara mesh, DS1 movement, drag cancellation/commit, undo/redo, copy save/reopen, unchanged JSON, the visibility toggle, and DS1 selection synchronization.

### Asset groups

Ctrl-click models in the viewport to add/remove them from the selection; Shift-click adds models. The entity list supports Ctrl/Shift multi-selection too. Drag any selected model to move the selection together, press Esc to cancel, and use F to frame the entire selection. One undo restores the complete move, including linked DS1 units, patrols, and collision. Shared collision belonging to other objects stays protected; a rejected destination rolls back the entire move.

For reusable groups, enter a name and choose **Group selected**. Clicking a group member selects its whole group. **Ungroup** removes the grouping without moving models or changing gameplay links. Group membership saves immediately beside the preset as `<preset>.rle-groups.json`; keep that file with your working preset to retain groups across launches. It is editor metadata and can be gitignored (`*.rle-groups.json` and `*.rle-groups.json.bak`). Game JSON does not gain parent entities or group fields. Save-copy exports do not carry this local grouping file automatically.

This first grouping implementation supports translation of unparented HD models. NPCs and terrain are edited separately; group rotation, scaling, and deletion are not included. Developer renderer check: `--group-smoke --smoke-output <folder>` with the same Act 1 preset/data arguments as the NPC smoke above. It works on copies inside the output folder.
