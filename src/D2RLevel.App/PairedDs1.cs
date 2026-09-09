using System.IO;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task<(LegacyFloorScene?, string)> LoadPairedDs1(PresetDocument preset, AssetResolver? assets, CancellationToken token)
    {
        if (assets is null) return (null, "DS1 unavailable: choose an asset folder to resolve the matching map and tiles.");
        try
        {
            if (openingWorkspaceSession?.Scene is { } workspaceScene && string.Equals(workspaceScene.JsonPath, preset.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var workspaceMap = await Task.Run(() => LegacyFloorScene.Load(workspaceScene.Ds1Path, assets, token), token);
                return (workspaceMap, "DS1: " + Path.GetFileName(workspaceScene.Ds1Path) + " · workspace scene");
            }
            if (PlacementLinks.LinkedDs1Path(preset.SourcePath) is { } linkedPath)
            {
                if (!File.Exists(linkedPath)) return (null, "Saved links require missing DS1: " + linkedPath);
                var linkedScene = await Task.Run(() => LegacyFloorScene.Load(linkedPath, assets, token), token);
                return (linkedScene, "DS1: " + Path.GetFileName(linkedPath) + " · saved object links");
            }
            var pair = PresetPairing.Find(preset.SourcePath, assets, settings.PresetPairs);
            if (pair is null) return (null, "No matching DS1 detected. Use Open DS1… to choose and remember the matching file.");
            var overrideRoot = pair.Source == "Base asset fallback" ? PresetPairing.Split(preset.SourcePath, "hd/env/preset")?.DataRoot : null;
            var scene = await Task.Run(() => LegacyFloorScene.Load(pair.Ds1Path, assets, token, overrideRoot), token);
            return (scene, $"DS1: {Path.GetFileName(pair.Ds1Path)} · {pair.Source}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, "DS1 could not load: " + ex.Message + " Use Open DS1… to select a file."); }
    }
}
