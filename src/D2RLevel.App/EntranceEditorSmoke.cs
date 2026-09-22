using System.IO;
using System.Text;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyEntrances(string output)
    {
        Directory.CreateDirectory(output);
        var original = document ?? throw new InvalidOperationException("Load expansion/mountaintop/mtntop.json.");
        var originalMap = pairedScene!.Collision!.Document;
        byte[] sourceJson = File.ReadAllBytes(original.SourcePath), sourceMap = File.ReadAllBytes(originalMap.SourcePath);
        string root = Path.GetFullPath(Path.Combine(output, "mod", "data"));
        string relativeJson = PresetPairing.Split(original.SourcePath, "hd/env/preset")!.Value.Relative;
        string relativeMap = PresetPairing.Split(originalMap.SourcePath, "global/tiles")!.Value.Relative;
        string jsonPath = Path.Combine(root, "hd", "env", "preset", relativeJson), mapPath = Path.Combine(root, "global", "tiles", relativeMap);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllBytes(jsonPath, sourceJson); File.WriteAllBytes(mapPath, sourceMap);
        await LoadWorkspace(root); await OpenWorkspaceScene(workspaceScenes.Single());
        var map = pairedScene!.Collision!.Document;
        var browser = CreateEntranceEditor(); browser.Owner = this; browser.Show();
        try
        {
            var marker = map.ExitTiles().First(t => t.IsExit && t.Main == 0);
            browser.SourceMap.ClickTile(marker.X, marker.Y);
            if (browser.SelectedSlot != 0) throw new InvalidOperationException("Exit marker selection failed.");
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-real-summit.png"));
            browser.SourceMap.ClickTile(-1, -1);
            if (browser.SelectedSlot is not null || !map.Serialize().SequenceEqual(sourceMap)) throw new InvalidOperationException("Empty map space did not clear selection safely.");
            browser.SelectSource(0); browser.ArmMove(); browser.MoveTo(-1, -1);
            if (!map.Serialize().SequenceEqual(sourceMap)) throw new InvalidOperationException("Invalid exit destination changed bytes.");
            var probe = Ds1CollisionDocument.Load(mapPath); (int X, int Y)? destination = null;
            var candidates = from y in Enumerable.Range(1, map.Height - 2) from x in Enumerable.Range(1, map.Width - 2)
                orderby Math.Abs(x - marker.X) + Math.Abs(y - marker.Y) select (x, y);
            foreach (var (x, y) in candidates)
            {
                if (x == marker.X && y == marker.Y) continue;
                try { probe.MoveExit(0, x, y); destination = (x, y); break; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
            }
            if (destination is not { } point) throw new InvalidOperationException("No supported exit move destination in real map.");
            browser.SourceMap.ClickTile(point.X, point.Y);
            if (map.Serialize().SequenceEqual(sourceMap) || placementLinks!.Warning is not null) throw new InvalidOperationException("Exit move failed: " + browser.StatusText);
            var moved = map.Serialize(); document!.Undo();
            if (!map.Serialize().SequenceEqual(sourceMap)) throw new InvalidOperationException("One undo did not restore the real exit group.");
            document.Redo(); if (!map.Serialize().SequenceEqual(moved)) throw new InvalidOperationException("Exit redo failed.");
            browser.Width = 1050; browser.Height = 700;
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-real-1050.png"));
        }
        finally { browser.Close(); }
        workspaceSession!.Save(document!, map, placementLinks!, connectionEdits);
        if (map.IsDirty || File.Exists(Path.Combine(root, "global", "excel", "levels.txt"))) throw new InvalidOperationException("A marker-only save wrote an unrelated Levels override.");
        // A fixture graph using copied real maps exercises the full authoring UI without altering base links.
        string excel = Path.Combine(root, "global", "excel"); Directory.CreateDirectory(excel);
        var sourceContext = LevelProperties.Load(original.SourcePath, originalMap.SourcePath, resolver).Contexts.Single();
        string[] relativeMaps = [relativeMap.Replace('\\', '/'), "entrance-fixture/b.ds1", "entrance-fixture/c.ds1", "entrance-fixture/d.ds1"];
        for (int i = 1; i < relativeMaps.Length; i++) { string path = Path.Combine(root, "global", "tiles", relativeMaps[i]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, sourceMap); }
        string levels = "Name\tId\tAct\tDrlgType\tLevelType\tVis0\tWarp0\tVis1\tWarp1\r\nSummit north\t120\t4\t2\t1\t121\t7\t0\t7\r\nOld north landing\t121\t4\t2\t1\t120\t7\t0\t7\r\nSummit south\t122\t4\t2\t1\t123\t7\t0\t7\r\nOld south landing\t123\t4\t2\t1\t122\t7\t0\t7\r\n";
        levels = levels.Replace("\t4\t2\t1\t", "\t4\t2\t" + sourceContext.Level!["LevelType"] + "\t");
        File.WriteAllText(Path.Combine(excel, "levels.txt"), levels, new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(excel, "lvlprest.txt"), "Name\tDef\tLevelId\tScan\tDt1Mask\tFile1\n" + string.Join("\n", Enumerable.Range(0, 4).Select(i => $"Summit {i}\t{i + 1}\t{i + 120}\t1\t{sourceContext.Preset["Dt1Mask"]}\t{relativeMaps[i]}")));
        File.WriteAllText(Path.Combine(excel, "lvlwarp.txt"), "Name\tId\tDirection\tOffsetX\tOffsetY\tExitWalkX\tExitWalkY\nFixture exit\t7\tb\t0\t0\t0\t0\n");
        connectionEdits = null;
        browser = CreateEntranceEditor(); browser.Owner = this; browser.Show();
        try
        {
            browser.SelectSource(0); browser.SelectTarget(122, null);
            var targetMarker = originalMap.ExitTiles().First(t => t.IsExit && t.Main == 0); browser.TargetMap.ClickTile(targetMarker.X, targetMarker.Y);
            browser.PreviewChange();
            if (!browser.HasPlan) throw new InvalidOperationException("Supported fixture route swap did not preview: " + browser.StatusText);
            browser.SearchAreas("no-area-matches");
            if (browser.HasPlan) throw new InvalidOperationException("Empty candidate filter retained an applicable connection preview.");
            browser.SearchAreas("south"); browser.SelectTarget(122, 0); browser.PreviewChange();
            if (!browser.HasPlan) throw new InvalidOperationException("Candidate name search did not restore a valid preview.");
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-connection-preview.png"));
            browser.ApplyChange();
            if (connectionEdits?.IsDirty != true || connectionEdits.Table.Rows.Single(r => r["Id"] == "120")["Vis0"] != "122") throw new InvalidOperationException("Apply did not stage reviewed route swap: " + browser.StatusText);
            document!.Undo(); if (connectionEdits.IsDirty) throw new InvalidOperationException("Connection undo did not restore original graph.");
            document.Redo(); if (!connectionEdits.IsDirty) throw new InvalidOperationException("Connection redo did not restore staged graph.");
            browser.Width = 1050; browser.Height = 700;
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-connections-1050.png"));
        }
        finally { browser.Close(); }
        workspaceSession.Save(document!, map, placementLinks!, connectionEdits);
        var expectedMap = map.Serialize();
        await OpenWorkspaceScene(workspaceScenes.Single());
        var graph = new EntranceConnections(resolver, root);
        if (graph.Connection(new(120, 0)).Destination != 122 || graph.Connection(new(121, 0)).Destination != 123 || !pairedScene!.Collision!.Document.Serialize().SequenceEqual(expectedMap) || placementLinks!.Warning is not null)
            throw new InvalidOperationException("Saved map and reciprocal route changes did not reopen.");
        if (!File.ReadAllBytes(original.SourcePath).SequenceEqual(sourceJson) || !File.ReadAllBytes(originalMap.SourcePath).SequenceEqual(sourceMap)) throw new InvalidOperationException("Smoke changed source assets.");
        File.WriteAllText(Path.Combine(output, "entrance-smoke.txt"), "PASS real Arreat Summit markers and game-table destinations, empty-space deselect, rejected coordinates, hidden-exit move, shared undo/redo, marker-only save without Levels override, four-area fixture preview/apply/undo/redo, combined scene/table save and reopen, source preservation. WPF screenshots at 1280x850 and 1050x700. Fixture routes are not a live-game compatibility test.");
    }
}
