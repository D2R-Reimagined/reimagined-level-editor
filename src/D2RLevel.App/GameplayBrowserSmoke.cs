using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyGameplayBrowser(string output)
    {
        Directory.CreateDirectory(output);
        var original = document ?? throw new InvalidOperationException("Load an Act 1 town scene.");
        var originalMap = pairedScene!.Collision!.Document;
        var jsonBefore = File.ReadAllBytes(original.SourcePath); var mapBefore = File.ReadAllBytes(originalMap.SourcePath);
        string root = Path.GetFullPath(Path.Combine(output, "mod", "data"));
        string jsonPath = Path.Combine(root, "hd", "env", "preset", PresetPairing.Split(original.SourcePath, "hd/env/preset")!.Value.Relative);
        string mapPath = Path.Combine(root, "global", "tiles", PresetPairing.Split(originalMap.SourcePath, "global/tiles")!.Value.Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllBytes(jsonPath, jsonBefore); File.WriteAllBytes(mapPath, mapBefore);
        await LoadWorkspace(root); await OpenWorkspaceScene(workspaceScenes.Single());
        var map = pairedScene!.Collision!.Document; int initialCount = map.Units.Count;
        var browser = CreateGameplayBrowser(); browser.Owner = this; browser.Show(); await browser.Ready;
        try
        {
            if (browser.StatusText.Length > 0) throw new InvalidOperationException("Real table catalog reported a warning: " + browser.StatusText);
            browser.SearchFor("akara");
            browser.FilterCategory(GameplayAssetKind.Object);
            if (browser.SelectKey("akara", GameplayAssetKind.Npc)) throw new InvalidOperationException("Category filter did not exclude NPCs.");
            browser.FilterCategory(GameplayAssetKind.Npc);
            if (!browser.SelectKey("akara", GameplayAssetKind.Npc) || !browser.CanPlace) throw new InvalidOperationException("Act 1 Akara is not placeable.");
            await browser.PreviewReady;
            if (!browser.HasMesh) throw new InvalidOperationException("Real Akara preview did not load.");
            int id = browser.Selection!.Id!.Value;
            browser.SetCoordinates(-1, 20); browser.PlaceSelected();
            if (map.Units.Count != initialCount) throw new InvalidOperationException("Out-of-bounds placement mutated the map.");
            browser.SetCoordinates(20, 20); browser.SetFlags("-1"); browser.PlaceSelected();
            if (map.Units.Count != initialCount) throw new InvalidOperationException("Invalid unsigned flags mutated gameplay.");
            browser.SetFlags("0x1"); browser.PlaceSelected();
            if (map.Units.Count != initialCount + 1 || map.Units.Last() is not { Type: 1, X: 20, Y: 20, Flags: 1 } placed || placed.Id != id
                || !document!.Serialize().SequenceEqual(jsonBefore) || npcItems.Last().IsPlaceholder)
                throw new InvalidOperationException("NPC placement did not preserve the recipe and HD preview. " + browser.StatusText);
            Undo_Click(this, new());
            if (!map.Serialize().SequenceEqual(mapBefore)) throw new InvalidOperationException("One undo did not remove the placement.");
            Redo_Click(this, new());
            if (map.Units.Count != initialCount + 1 || placementLinks!.Warning is not null) throw new InvalidOperationException("Redo did not restore valid gameplay links.");
            await CaptureAuthoring(browser, Path.Combine(output, "gameplay-browser-akara.png"));
            browser.SearchFor("nothing-matches-this-filter"); await browser.PreviewReady;
            if (browser.CanPlace || browser.Selection is not null || browser.HasMesh) throw new InvalidOperationException("Empty filter retained a placement or preview.");
            browser.FilterCategory(null); browser.SearchFor("LargeChestR");
            if (!browser.SelectKey("LargeChestR", GameplayAssetKind.Object) || !browser.CanPlace) throw new InvalidOperationException("Act 1 chest not resolved.");
            await browser.PreviewReady;
            if (!browser.HasMesh) throw new InvalidOperationException("Real chest preview did not load.");
            int chestId = browser.Selection!.Id!.Value;
            browser.SetCoordinates(25, 25); browser.PlaceSelected();
            if (map.Units.Count != initialCount + 2 || map.Units.Last().Type != 2 || map.Units.Last().Id != chestId)
                throw new InvalidOperationException("Object placement wrote the wrong preset ID. " + browser.StatusText);
            browser.Width = 950; browser.Height = 650;
            await CaptureAuthoring(browser, Path.Combine(output, "gameplay-browser-chest-950.png"));
            browser.SearchFor("Bishibosh");
            if (!browser.SelectKey("Bishibosh", GameplayAssetKind.Superunique) || !browser.CanPlace) throw new InvalidOperationException("Superunique catalog resolution failed.");
            await browser.PreviewReady;
            if (!browser.HasMesh) throw new InvalidOperationException("Superunique base-class preview failed.");
            browser.SearchFor("place_nothing", false);
            if (!browser.SelectKey("place_nothing", GameplayAssetKind.Spawn) || browser.CanPlace) throw new InvalidOperationException("Unsupported runtime spawn became placeable.");
            await browser.PreviewReady;
            browser.PlaceSelected();
            if (map.Units.Count != initialCount + 2) throw new InvalidOperationException("Disabled placement edited gameplay.");
            // External table edits must invalidate an already selected ID before insertion.
            browser.SearchFor("akara"); browser.SelectKey("akara", GameplayAssetKind.Npc); await browser.PreviewReady;
            string excel = Path.Combine(root, "global", "excel"); Directory.CreateDirectory(excel);
            File.WriteAllText(Path.Combine(excel, "monpreset.txt"), "Act\tPlace\n1\tgheed\n");
            browser.PlaceSelected();
            if (map.Units.Count != initialCount + 2 || !browser.StatusText.Contains("mapping changed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Stale saved table mapping was not rejected.");
            await browser.RefreshCatalog(); browser.SearchFor("akara", false);
            if (!browser.SelectKey("akara", GameplayAssetKind.Npc) || browser.CanPlace) throw new InvalidOperationException("Catalog refresh retained the removed mapping.");
            await browser.PreviewReady;
            File.Delete(Path.Combine(excel, "monpreset.txt"));
        }
        finally { browser.Close(); }
        var expected = map.Serialize();
        SaveScene_Click(this, new()); await LoadPreset(jsonPath);
        if (!pairedScene!.Collision!.Document.Serialize().SequenceEqual(expected) || placementLinks!.Warning is not null
            || !File.ReadAllBytes(jsonPath).SequenceEqual(jsonBefore)) throw new InvalidOperationException("Gameplay placements did not save/reopen cleanly.");
        if (!File.ReadAllBytes(original.SourcePath).SequenceEqual(jsonBefore) || !File.ReadAllBytes(originalMap.SourcePath).SequenceEqual(mapBefore))
            throw new InvalidOperationException("Browser smoke changed original assets.");
        File.WriteAllText(Path.Combine(output, "gameplay-browser-smoke.txt"),
            "PASS real Act 1 catalogs and Akara/chest/superunique meshes; search, empty selection, unsupported entry, invalid coordinates, exact NPC/object preset IDs, shared undo/redo, immediate NPC preview, stale table rejection, paired save/reopen and source preservation. WPF screenshots at 1120x800 and 950x650. Game runtime not tested.");
    }
}
