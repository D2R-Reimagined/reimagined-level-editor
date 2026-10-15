# Blank editor windows

If the editor exposes responsive controls but its client area appears blank, launch
it with `--software-rendering`. This sets WPF's process-local `SoftwareOnly`
rendering preference before creating windows. It does not change Windows registry
settings, save a global preference, or alter map data. Omit the flag on the next
launch to restore the normal default. Software rendering may reduce viewport
performance; it is an opt-in fallback, not the default renderer.

Use `--settings-file <path>` when running isolated diagnostic instances. The
diagnostics pane reports the process rendering mode.

## Local comparison, 2026-09-23

The unchanged portable release identified itself as
`0.8.0+3acd0cf2106f071726531d61a196171b27a348fa`. It and the modified build loaded
the original `caveecrow` fixture with 263/263 model instances and no missing or
unsupported markers, while their captured client areas were blank white.

The same modified executable was then compared with and without the flag, using
separate settings files and the same fixture, extraction and decoder:

| Mode | Observed result |
| --- | --- |
| Default | Blank white capture after successful model loading |
| SoftwareOnly | Main UI and HD cave preview visible; DS1 window visible; ground-extension overview visible |

The diagnostic build completed with zero warnings and errors. Both modes were
tested through the actual editor and the same computer-use capture tool. This
establishes a usable fallback and demonstrates that the terrain-extension changes
are not required to trigger the symptom. It does not identify a specific GPU,
driver or capture-component defect, or establish what a human saw on the physical
monitor. No Windows graphics setting or running game was changed.

This rendering check is not terrain-boundary, collision, cancellation or in-game
qualification. The source fixture still has documented incomplete asset/table
coverage. Keep those gates separate.

The process setting is documented by Microsoft:
https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.processrendermode
