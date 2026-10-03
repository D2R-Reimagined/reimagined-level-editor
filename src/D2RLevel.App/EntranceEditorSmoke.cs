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
            // Hovering an exit draws its warp and describes where it leads.
            browser.SourceMap.HoverTile(marker.X, marker.Y);
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-warp-hover.png"));
            if (browser.SourceMap.HoveredSlot != 0 || browser.SourceMap.CardLines.Count < 2 || !browser.SourceMap.CardLines[0].StartsWith(L.T("Exit {0} → {1}", 0, "")))
                throw new InvalidOperationException("Hovering an exit did not describe its warp: " + string.Join(" | ", browser.SourceMap.CardLines));
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
            // While a move is armed the group follows the pointer: green where it can go, red with the reason where it cannot.
            browser.SourceMap.HoverTile(map.Width - 1, map.Height - 1);
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-move-blocked.png"));
            if (browser.SourceMap.CardLines.FirstOrDefault() != L.T("Exit {0} cannot move here", 0) || !map.Serialize().SequenceEqual(sourceMap))
                throw new InvalidOperationException("Placing over the map border was not shown as blocked: " + string.Join(" | ", browser.SourceMap.CardLines));
            browser.SourceMap.HoverTile(point.X, point.Y);
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-move-preview.png"));
            if (browser.SourceMap.CardLines.FirstOrDefault() != L.T("Exit {0}: click to move here", 0) || !map.Serialize().SequenceEqual(sourceMap))
                throw new InvalidOperationException("Placing over a valid tile was not previewed: " + string.Join(" | ", browser.SourceMap.CardLines));
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
        // A clickable warp shaped like vanilla's "Act 1 Cave Up", so the hover overlay draws a selection box.
        File.WriteAllText(Path.Combine(excel, "lvlwarp.txt"), "Name\tId\tSelectX\tSelectY\tSelectDX\tSelectDY\tExitWalkX\tExitWalkY\tOffsetX\tOffsetY\tLitVersion\tTiles\tNoInteract\tDirection\n"
            + "Fixture exit\t7\t-30\t-120\t120\t150\t3\t5\t2\t5\t1\t2\t0\tb\n");
        connectionEdits = null;
        browser = CreateEntranceEditor(); browser.Owner = this; browser.Show();
        try
        {
            browser.SelectSource(0); browser.SelectTarget(122, null);
            var targetMarker = originalMap.ExitTiles().First(t => t.IsExit && t.Main == 0);
            var sourceMarker = map.ExitTiles().First(t => t.IsExit && t.Main == 0);
            browser.SourceMap.HoverTile(sourceMarker.X, sourceMarker.Y);
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-warp-selection.png"));
            if (!browser.SourceMap.CardLines.Contains(L.T("Selection box: {0}, {1} · {2} × {3} pixels", -30, -120, 120, 150)) || !browser.SourceMap.CardLines.Contains(L.T("Arrive at {0}, {1} · walk to {2}, {3} subtiles", 2, 5, 3, 5)))
                throw new InvalidOperationException("The hover card did not read the fixture warp: " + string.Join(" | ", browser.SourceMap.CardLines));
            browser.TargetMap.ClickTile(targetMarker.X, targetMarker.Y);
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
            // A slot the map does not use yet: suggest places for it, take one, then place one by hand.
            async Task Rendered() { browser.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); }
            int unused = Enumerable.Range(2, 6).First(s => !map.ExitTiles().Any(t => t.IsExit && t.Main == s));
            var beforeAdd = map.Serialize();
            browser.SelectSource(unused); browser.SuggestSpots();
            var spots = browser.SourceMap.Candidates;
            if (spots.Count < 2 || !map.Serialize().SequenceEqual(beforeAdd) || spots.Any(c => map.ExitAddProblem(unused, c.X, c.Y) is not null))
                throw new InvalidOperationException("Exit suggestions failed, changed the map or offered an invalid tile: " + browser.StatusText);
            browser.SourceMap.HoverTile(spots[0].X, spots[0].Y); await Rendered();
            if (browser.SourceMap.CardLines.FirstOrDefault() != L.T("Suggestion {0}: tile {1}, {2} · click to place exit {3}", 1, spots[0].X, spots[0].Y, unused))
                throw new InvalidOperationException("Hovering a suggestion did not describe it: " + string.Join(" | ", browser.SourceMap.CardLines));
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-suggestions.png"));
            browser.SourceMap.ChooseCandidate(0);
            var added = map.ExitTiles().Where(t => t.IsExit && t.Main == unused).ToArray();
            if (added is not [{ Hidden: true } first] || (first.X, first.Y) != (spots[0].X, spots[0].Y) || placementLinks!.Warning is not null)
                throw new InvalidOperationException("Choosing a suggestion did not add the hidden exit there: " + browser.StatusText);
            browser.SelectSource(unused); browser.SuggestSpots();
            if (browser.SourceMap.Candidates.Count == 0 || browser.SourceMap.Candidates.Any(c => (c.X, c.Y) == (first.X, first.Y)))
                throw new InvalidOperationException("A placed exit was not offered alternative spots.");
            document.Undo(); if (!map.Serialize().SequenceEqual(beforeAdd)) throw new InvalidOperationException("One undo did not remove the added exit.");
            browser.SelectSource(unused); browser.ArmAdd();
            browser.SourceMap.HoverTile(map.Width - 1, map.Height - 1); await Rendered();
            if (browser.SourceMap.CardLines.FirstOrDefault() != L.T("Exit {0} cannot be added here", unused))
                throw new InvalidOperationException("Adding over the map border was not shown as blocked: " + string.Join(" | ", browser.SourceMap.CardLines));
            browser.SourceMap.HoverTile(spots[1].X, spots[1].Y); await Rendered();
            if (browser.SourceMap.CardLines.FirstOrDefault() != L.T("Exit {0}: click to add a hidden exit here", unused))
                throw new InvalidOperationException("Adding over a valid tile was not previewed: " + string.Join(" | ", browser.SourceMap.CardLines));
            await CaptureAuthoring(browser, Path.Combine(output, "entrances-add-preview.png"));
            browser.SourceMap.ClickTile(spots[1].X, spots[1].Y);
            if (!map.ExitTiles().Any(t => t.IsExit && t.Main == unused && (t.X, t.Y) == (spots[1].X, spots[1].Y))) throw new InvalidOperationException("Manual exit placement failed: " + browser.StatusText);
            document.Undo(); if (!map.Serialize().SequenceEqual(beforeAdd)) throw new InvalidOperationException("Manual exit placement did not undo.");
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
        File.WriteAllText(Path.Combine(output, "entrance-smoke.txt"), "PASS real Arreat Summit markers and game-table destinations, empty-space deselect, rejected coordinates, hidden-exit move, shared undo/redo, marker-only save without Levels override, four-area fixture preview/apply/undo/redo, combined scene/table save and reopen, source preservation. WPF screenshots at 1280x850 and 1050x700. Fixture routes are not a live-game compatibility test.\n"
            + "PASS exit placement on the real map: ranked suggestions for an unused slot pass the add check without changing bytes, a suggestion's card, choosing one adds a hidden exit there, a placed exit gets alternative spots, armed manual placement shows blocked and valid tiles, each addition undoes in one step.");
    }
}
