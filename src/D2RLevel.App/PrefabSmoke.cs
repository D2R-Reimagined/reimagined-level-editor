using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyPrefabs(string output)
    {
        Directory.CreateDirectory(output);
        var original = document ?? throw new InvalidOperationException("Load an Act 1 town preset."); var originalMap = pairedScene!.Collision!.Document;
        byte[] originalJsonBytes = File.ReadAllBytes(original.SourcePath), originalMapBytes = File.ReadAllBytes(originalMap.SourcePath);
        string root = Path.GetFullPath(Path.Combine(output, "mod", "data"));
        string jsonPath = Path.Combine(root, "hd", "env", "preset", PresetPairing.Split(original.SourcePath, "hd/env/preset")!.Value.Relative);
        string mapPath = Path.Combine(root, "global", "tiles", PresetPairing.Split(originalMap.SourcePath, "global/tiles")!.Value.Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!); File.WriteAllBytes(jsonPath, originalJsonBytes); File.WriteAllBytes(mapPath, originalMapBytes);
        await LoadWorkspace(root); await OpenWorkspaceScene(workspaceScenes.Single());
        var map = pairedScene!.Collision!.Document; var current = document!; var links = placementLinks!; double scale = links.Calibration.UnitsPerTile;
        string[] supported = ["TransformDefinitionComponent", "ModelDefinitionComponent", "ModelVariationDefinitionComponent", "ModelPlatformTierComponent", "PhysicsBodyDefinitionComponent"];
        var candidates = current.Entities.Where(e => GroupMovement.CanGroup(e) && e.Components.All(c => supported.Contains(c["type"]?.GetValue<string>())) && Scene.GetItem(e) is { IsPlaceholder: false })
            .OrderByDescending(e => e.PreviewModel!.Contains("barrel", StringComparison.OrdinalIgnoreCase) || e.PreviewModel.Contains("crate", StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => Scene.GetItem(e)!.TexturePaths?.Count ?? 0).ToArray();
        PresetEntity? model = null; int sx = 0, sy = 0;
        foreach (var candidate in candidates)
        {
            int x = (int)Math.Floor(candidate.Transform.Position.X / scale), y = (int)Math.Floor(candidate.Transform.Position.Z / scale);
            if (x < 1 || y < 1 || x >= map.Width - 3 || y >= map.Height - 3 || !map.CanBlock(x, y) || !map.CanBlock(x + 1, y)) continue;
            if (map.Units.Any(u => u.X == x * 5 + 2 && u.Y == y * 5 + 2 || u.X == x * 5 + 7 && u.Y == y * 5 + 2)) continue;
            model = candidate; sx = x; sy = y; break;
        }
        if (model is null) throw new InvalidOperationException("No supported real prop found for prefab smoke.");
        // Add a direct Fallen recipe in this disposable mod; vanilla uses hardcoded pack markers.
        string excel = Path.Combine(root, "global", "excel"); Directory.CreateDirectory(excel);
        File.WriteAllText(Path.Combine(excel, "monpreset.txt"), File.ReadAllText(resolver!.Resolve("data/global/excel/monpreset.txt")).TrimEnd() + "\r\n1\tfallen1\r\n");
        var catalog = GameplayCatalog.Load(resolver, root, map.Act);
        var recipe = catalog.Assets.First(a => a.Kind == GameplayAssetKind.Monster && a.CanPlace && a.Key.StartsWith("fallen", StringComparison.OrdinalIgnoreCase));
        int first = links.AppendUnit(1, recipe.Id!.Value, sx * 5 + 2, sy * 5 + 2, 0), second = links.AppendUnit(1, recipe.Id.Value, sx * 5 + 7, sy * 5 + 2, 0);
        map.InsertPathPoint(first, 0, sx * 5 + 3, sy * 5 + 2, 1); map.InsertPathPoint(first, 1, sx * 5 + 4, sy * 5 + 2, 1);
        links.LinkUnit(model, first, scale); links.LinkFootprint(model, [new(sx, sy)], scale, false);
        await LoadScene(current, resolver); SetSelection([model]);
        workspaceSession!.Save(current, map, links); var sourceJson = current.Serialize(); var sourceMap = map.Serialize();
        var browser = CreatePrefabBrowser(); browser.Owner = this; browser.Show(); string package;
        try
        {
            if (!browser.EmptyStateText.Contains("No gameplay prefabs saved yet", StringComparison.Ordinal)) throw new InvalidOperationException("Prefab browser did not explain its empty library.");
            await CaptureAuthoring(browser, Path.Combine(output, "prefab-empty-library.png"));
            Exception? dialogFailure = null;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(async () =>
            {
                Window? dialog = null;
                try { dialog = browser.OwnedWindows.Cast<Window>().Single(); await CaptureAuthoring(dialog, Path.Combine(output, "prefab-capture.png")); }
                catch (Exception ex) { dialogFailure = ex; }
                finally { dialog?.Close(); }
            }));
            await browser.CaptureDialog(); if (dialogFailure is not null) throw dialogFailure;
            package = await browser.SavePrefab("Fallen supply camp", [second], scale);
            if (!browser.CanPlace) throw new InvalidOperationException("Saved prefab did not become placeable: " + browser.StatusText);
            if (!current.Serialize().SequenceEqual(sourceJson) || !map.Serialize().SequenceEqual(sourceMap)) throw new InvalidOperationException("Prefab capture changed the source scene.");
            var prefab = GameplayPrefab.Load(package); prefab.VerifyAssets(package);
            if (prefab.Models.Length != 1 || prefab.Units.Length != 2 || prefab.Assets.Length == 0 || prefab.Models[0].Collision.Length != 1 || prefab.Units[0].Patrol.Length != 2) throw new InvalidOperationException("Real prefab omitted a component of the assembly.");
            await CaptureAuthoring(browser, Path.Combine(output, "prefab-library.png"));
            browser.SearchFor("no-matching-prefab"); await browser.PreviewReady;
            if (browser.CanPlace) throw new InvalidOperationException("Empty prefab search retained placement state.");
            browser.LoadFolder(package); await browser.PreviewReady;
            browser.Coordinates(-1, -1); await browser.PlaceSelected();
            if (!map.Serialize().SequenceEqual(sourceMap) || !current.Serialize().SequenceEqual(sourceJson)) throw new InvalidOperationException("Rejected placement changed the scene.");
            (int X, int Y)? point = null;
            var spots = from y in Enumerable.Range(1, map.Height - 2) from x in Enumerable.Range(1, map.Width - 2) orderby Math.Abs(x - sx) + Math.Abs(y - sy) select (x, y);
            foreach (var (x, y) in spots)
            {
                if (Math.Abs(x - sx) + Math.Abs(y - sy) < 3) continue;
                try { links.ValidatePrefabPlacement(prefab, x, y, model.Transform.Position.Y, scale, catalog); point = (x, y); break; } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { }
            }
            if (point is not { } target) throw new InvalidOperationException("No suitable real-map prefab anchor found.");
            browser.Coordinates(target.X, target.Y, model.Transform.Position.Y); int count = current.Entities.Count, units = map.Units.Count;
            await browser.PlaceSelected();
            if (current.Entities.Count != count + 1 || map.Units.Count != units + 2 || links.Warning is not null || links.HasBrokenLinks) throw new InvalidOperationException("Real prefab placement failed: " + browser.StatusText);
            var copy = current.Entities.Last(); if (Scene.GetItem(copy) is null) throw new InvalidOperationException("New prefab HD model was not rendered.");
            var placedJson = current.Serialize(); var placedMap = map.Serialize();
            current.Undo(); if (!current.Serialize().SequenceEqual(sourceJson) || !map.Serialize().SequenceEqual(sourceMap) || Scene.GetItem(copy) is not null) throw new InvalidOperationException("Prefab shared undo did not restore scene and viewport.");
            current.Redo(); if (!current.Serialize().SequenceEqual(placedJson) || !map.Serialize().SequenceEqual(placedMap) || Scene.GetItem(copy) is null) throw new InvalidOperationException("Prefab shared redo did not restore assembly and viewport.");
            browser.Width = 1020; browser.Height = 720; await CaptureAuthoring(browser, Path.Combine(output, "prefab-library-1020.png"));
            string dependency = Path.Combine(package, "assets", prefab.Assets.Last().File); var dependencyBytes = File.ReadAllBytes(dependency);
            File.WriteAllBytes(dependency, [.. dependencyBytes, 0]); browser.LoadFolder(package); await browser.PreviewReady;
            if (browser.CanPlace) throw new InvalidOperationException("Tampered prefab retained enabled placement."); File.WriteAllBytes(dependency, dependencyBytes);
        }
        finally { browser.Close(); }
        var finalJson = current.Serialize(); var finalMap = map.Serialize(); workspaceSession!.Save(current, map, links);
        await OpenWorkspaceScene(workspaceScenes.Single());
        if (!document!.Serialize().SequenceEqual(finalJson) || !pairedScene!.Collision!.Document.Serialize().SequenceEqual(finalMap) || placementLinks!.HasBrokenLinks || placementLinks.Warning is not null) throw new InvalidOperationException("Prefab scene did not survive save/reopen.");
        if (!File.ReadAllBytes(original.SourcePath).SequenceEqual(originalJsonBytes) || !File.ReadAllBytes(originalMap.SourcePath).SequenceEqual(originalMapBytes)) throw new InvalidOperationException("Prefab smoke modified original assets.");
        File.WriteAllText(Path.Combine(output, "prefab-smoke.txt"), "PASS real prop mesh and texture bundle, Fallen gameplay recipes, explicit membership plus ownership closure, patrols and collision, source-preserving capture, empty search, rejected placement, assembled insertion, viewport and byte-exact shared undo/redo, tamper rejection, workspace save/reopen. WPF captures at 1220x850 and 1020x720. Game runtime and binary asset dependency formats beyond known model textures/JSON references are not verified.");
    }
}
