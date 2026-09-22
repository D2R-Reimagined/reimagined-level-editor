using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyLevelProperties(string output)
    {
        Directory.CreateDirectory(output);
        var original = document ?? throw new InvalidOperationException("Load an Act 1 town preset for this smoke.");
        var map = pairedScene ?? throw new InvalidOperationException(pairedStatus);
        var assets = resolver ?? throw new InvalidOperationException("Choose extracted assets.");
        byte[] jsonBefore = File.ReadAllBytes(original.SourcePath), mapBefore = File.ReadAllBytes(map.Ds1Path);
        var initial = LevelProperties.Load(original.SourcePath, map.Ds1Path, assets);
        if (initial.Contexts is not [{ Level: not null }]) throw new InvalidOperationException("Expected one real preset/area context.");

        // Use a disposable workspace so a saved table refresh exercises the real load path.
        string root = Path.Combine(output, "workspace", "data");
        string json = Path.Combine(root, "hd", "env", "preset", PresetPairing.Split(original.SourcePath, "hd/env/preset")!.Value.Relative);
        string ds1 = Path.Combine(root, "global", "tiles", PresetPairing.Split(map.Ds1Path, "global/tiles")!.Value.Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(json)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1)!);
        File.WriteAllBytes(json, jsonBefore); File.WriteAllBytes(ds1, mapBefore);
        string excel = Path.Combine(root, "global", "excel"); Directory.CreateDirectory(excel);
        string levelsPath = Path.GetFullPath(Path.Combine(excel, "levels.txt"));
        var lines = File.ReadAllLines(initial.Sources.Single(t => t.Name == "levels").SourcePath!);
        var columns = lines[0].TrimStart('\uFEFF').Split('\t');
        int row = initial.Contexts[0].Level!.Line - 1;
        var cells = lines[row].Split('\t'); Array.Resize(ref cells, columns.Length);
        void Set(string column, string value) => cells[Array.IndexOf(columns, column)] = value;
        Set("MonLvlEx", "11"); Set("MonLvlEx(N)", "44"); Set("MonLvlEx(H)", "88");
        void SaveTable() { lines[row] = string.Join('\t', cells); File.WriteAllLines(levelsPath, lines); }
        SaveTable();
        await LoadPreset(json);
        ShowLevelProperties(true);
        if (LevelDetails.ContextCount != 1 || !LevelDetails.DetailsText.Contains("\n11\n") || !LevelDetails.DetailsText.Contains(levelsPath))
            throw new InvalidOperationException("Level panel did not resolve the workspace override and source.\n" + LevelDetails.DetailsText);
        LevelDetails.SelectDifficulty(1);
        if (!LevelDetails.DetailsText.Contains("\n44\n")) throw new InvalidOperationException("Nightmare control did not switch area values.");
        LevelDetails.SelectDifficulty(2);
        if (!LevelDetails.DetailsText.Contains("\n88\n")) throw new InvalidOperationException("Hell control did not switch area values.");
        Set("MonLvlEx(H)", "89"); SaveTable();
        await RefreshLevelProperties();
        if (!LevelDetails.DetailsText.Contains("\n89\n")) throw new InvalidOperationException("Saved table refresh did not update the panel.");
        WindowState = WindowState.Normal; Width = 1100; Height = 700;
        await CaptureAuthoring(this, Path.Combine(output, "level-properties-1100.png"));
        await CaptureElement(LevelDetails, Path.Combine(output, "level-properties-panel.png"));
        var current = LevelProperties.Load(json, ds1, assets);
        var second = current.Contexts[0] with { Level = null, Warning = "Reusable room" };
        LevelDetails.SetSnapshot(current with { Contexts = [current.Contexts[0], second], Warning = "Choose a preset context." });
        if (LevelDetails.DetailsText.Contains("Area record")) throw new InvalidOperationException("Ambiguous panel selected an area automatically.");
        LevelDetails.SelectContext(0);
        if (!LevelDetails.DetailsText.Contains("Area record")) throw new InvalidOperationException("Explicit context selection did not render the area.");
        LevelDetails.SelectContext(1);
        if (LevelDetails.DetailsText.Contains("Area record")) throw new InvalidOperationException("Reusable context retained stale area values.");
        ShowLevelProperties(false);
        if (EntityInspector.Visibility != Visibility.Visible || LevelDetails.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Object inspector did not return.");
        ShowWorkspaceExplorer();
        if (LevelDetails.ContextCount != 0) throw new InvalidOperationException("Workspace explorer retained stale level context.");
        if (!File.ReadAllBytes(original.SourcePath).SequenceEqual(jsonBefore) || !File.ReadAllBytes(map.Ds1Path).SequenceEqual(mapBefore)
            || !File.ReadAllBytes(json).SequenceEqual(jsonBefore) || !File.ReadAllBytes(ds1).SequenceEqual(mapBefore))
            throw new InvalidOperationException("Properties inspection changed scene data.");
        File.WriteAllText(Path.Combine(output, "level-properties-smoke.txt"),
            "PASS real preset/area resolution, workspace table override, Normal/Nightmare/Hell controls, saved table refresh, explicit ambiguous context selection, reusable-room clearing, object/level navigation, workspace clearing, unchanged source and copied maps, WPF render at 1100x700.\nGame runtime not tested.");
    }
}
