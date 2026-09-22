using System.Buffers.Binary;
using System.Text;
using D2RLevel.Core;

internal static class EntranceChecks
{
    private static Ds1CollisionDocument Map(string path, params (int X, int Y, int Slot, int Orientation, bool Hidden)[] exits)
    {
        var map = Ds1CollisionDocument.Create(path, 12, 12, 1, Ds1CollisionDocument.FloorKey(0, 0), wallLayers: 2); var bytes = map.Serialize();
        foreach (var t in exits)
        {
            int offset = map.Walls[0].Offset + (t.Y * map.Width + t.X) * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), 0x81u | (uint)t.Slot << 20 | (t.Hidden ? 0x80000000u : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + map.Width * map.Height * 4), (uint)t.Orientation | 0x71000000);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return Ds1CollisionDocument.Load(path);
    }
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string root = Path.Combine(folder, "entrances"), baseRoot = Path.Combine(folder, "entrance-assets");
        Directory.CreateDirectory(Path.Combine(root, "global", "excel")); Directory.CreateDirectory(Path.Combine(baseRoot, "hd")); Directory.CreateDirectory(Path.Combine(baseRoot, "global", "excel"));
        var assets = new AssetResolver(baseRoot);
        var map = Map(Path.Combine(root, "global", "tiles", "a.ds1"), (2, 2, 0, 10, true), (3, 2, 0, 10, true), (7, 7, 1, 11, false), (8, 8, 30, 10, true));
        var original = map.Serialize(); string fingerprint = map.LinkFingerprint();
        check(map.ExitTiles().Count == 4 && map.ExitTiles().Count(t => t.IsExit) == 3, "orientation 10 and 11 exits are distinct from main-30 special markers");
        check(map.ExitMoveWarning(0) is null && map.ExitMoveWarning(1) is not null && map.ExitMoveWarning(30) is not null, "only complete hidden ordinary exit groups can move");
        map.MoveExit(0, 3, 2);
        check(map.ExitTiles().Where(t => t.Main == 0).Select(t => t.X).SequenceEqual(new[] { 3, 4 }) && map.ExitTiles().All(t => t.Y is 2 or 7 or 8), "overlapping move translates every group tile exactly once");
        check(map.LinkFingerprint() != fingerprint && map.Walls[0].Orientations[2 * 12 + 2] == 0 && map.Walls[0].Orientations[2 * 12 + 4] == 10, "exit move updates cached orientations and requires new link fingerprint");
        var moved = map.Serialize(); map.History.Undo(); check(map.Serialize().SequenceEqual(original), "exit undo restores every byte including orientation flags");
        map.History.Redo(); check(map.Serialize().SequenceEqual(moved), "exit redo restores byte-exact moved group"); map.History.Undo();
        foreach (var point in new[] { (-1, 3), (10, 3), (7, 7), (8, 8) }) throws(() => map.MoveExit(0, point.Item1, point.Item2), "exit rejects bounds, border or occupied wall destination");
        check(map.Serialize().SequenceEqual(original), "rejected exit moves are atomic");
        map.Paint([(5, 5)], true); throws(() => map.MoveExit(0, 5, 5), "blocking override rejects exit relocation"); map.History.Undo();
        map.PaintFloor(0, [(5, 5)], 0); throws(() => map.MoveExit(0, 5, 5), "empty ground rejects exit relocation"); map.History.Undo();
        var separate = Map(Path.Combine(root, "separate.ds1"), (2, 2, 0, 10, true), (8, 2, 0, 10, true));
        throws(() => separate.MoveExit(0, 4, 4), "disconnected groups with the same slot are ambiguous");
        var layered = Map(Path.Combine(root, "layered.ds1"), (2, 2, 0, 10, true)); var layeredBytes = layered.Serialize();
        BinaryPrimitives.WriteUInt32LittleEndian(layeredBytes.AsSpan(layered.Walls[1].Offset + (5 * 12 + 5) * 4), 1);
        File.WriteAllBytes(layered.SourcePath, layeredBytes); layered = Ds1CollisionDocument.Load(layered.SourcePath);
        throws(() => layered.MoveExit(0, 5, 5), "walls on another layer reject exit relocation");
        string protectedJsonPath = Path.Combine(root, "protected.json"); File.WriteAllText(protectedJsonPath, "{\"entities\":[]}");
        foreach (var tile in new[] { new LinkedTile(2, 2), new LinkedTile(5, 5) })
        {
            var protectedMap = Map(Path.Combine(root, "protected.ds1"), (2, 2, 0, 10, true)); var protectedJson = PresetDocument.Load(protectedJsonPath);
            var entity = protectedJson.AddModel("data/hd/fixture.model", new(0, 0, 0)); var protectedLinks = new PlacementLinks(protectedJson, protectedMap);
            protectedLinks.LinkFootprint(entity, [tile], 10, false); var protectedBytes = protectedMap.Serialize();
            throws(() => protectedLinks.MoveExit(0, 5, 5), "owned source or destination footprint rejects exit relocation");
            check(protectedMap.Serialize().SequenceEqual(protectedBytes), "protected exit rejection preserves map and link structure");
        }
        Map(Path.Combine(root, "global", "tiles", "b.ds1"), (2, 2, 0, 11, true));
        Map(Path.Combine(root, "global", "tiles", "c.ds1"), (2, 2, 0, 10, true));
        Map(Path.Combine(root, "global", "tiles", "d.ds1"), (2, 2, 0, 11, true));
        string levelPath = Path.Combine(baseRoot, "global", "excel", "levels.txt");
        string levels = "\uFEFFName\tId\tAct\tDrlgType\tVis0\tWarp0\tVis1\tWarp1\tUnknown\r\n" +
            "Arène\t1\t0\t2\t2\t7\t0\t7\t保留\r\nB\t2\t0\t2\t1\t7\t0\t7\tkeep\r\nC\t3\t0\t2\t4\t7\t0\t7\tkeep\r\nD\t4\t0\t2\t3\t7\t0\t7\tkeep\r\n";
        File.WriteAllText(levelPath, levels, new UTF8Encoding(false));
        string presetPath = Path.Combine(root, "global", "excel", "lvlprest.txt"), warpPath = Path.Combine(root, "global", "excel", "lvlwarp.txt");
        string presets = "Name\tDef\tLevelId\tScan\tFile1\nA\t1\t1\t1\ta.ds1\nB\t2\t2\t1\tb.ds1\nC\t3\t3\t1\tc.ds1\nD\t4\t4\t1\td.ds1\n";
        File.WriteAllText(presetPath, presets);
        string warps = "Name\tId\tDirection\tOffsetX\tOffsetY\nLeft\t7\tl\t0\t0\nRight\t7\tr\t0\t0\n"; File.WriteAllText(warpPath, warps);
        string jsonPath = Path.Combine(root, "hd", "env", "preset", "a.json"); Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); File.WriteAllText(jsonPath, "{\"entities\":[]}");
        var json = PresetDocument.Load(jsonPath); var links = new PlacementLinks(json, map); links.ConnectWorkspace();
        var edits = new LevelTableEdits(levelPath, root, json.History); var graph = new EntranceConnections(assets, root, edits);
        check(graph.Connection(new(1, 0)) is { Destination: 2, Warp: 7, ReturnSlots: [0] }, "Vis destination and matching reverse slot resolve from game data");
        check(graph.WarpRows(7).Count == 2, "directional variants sharing a Warp Id are retained");
        var plan = graph.Plan(new(1, 0), new(3, 0));
        check(plan.Routes.SequenceEqual(new[] { (1, 0, 3), (3, 0, 1), (2, 0, 4), (4, 0, 2) }), "pair swap previews all four reciprocal route updates");
        graph.Apply(new(1, 0), new(3, 0));
        // Compare by numeric cell substitutions, retaining all other bytes including BOM and CRLF.
        string[] expectedLines = levels.Split("\r\n"); int[] destinations = [3, 4, 1, 2];
        for (int i = 1; i <= 4; i++) { var cells = expectedLines[i].Split('\t'); cells[4] = destinations[i - 1].ToString(); expectedLines[i] = string.Join('\t', cells); }
        check(edits.Serialize().SequenceEqual(Encoding.UTF8.GetBytes(string.Join("\r\n", expectedLines))), "connection patch preserves BOM, CRLF, row order, Unicode and unrelated columns");
        check(!File.Exists(edits.TargetPath) && graph.Connection(new(1, 0)).Destination == 3 && edits.IsDirty, "connection edits are staged and immediately visible without disk writes");
        json.Undo(); check(edits.Serialize().SequenceEqual(File.ReadAllBytes(levelPath)) && !edits.IsDirty, "shared undo restores the entire reciprocal pair swap");
        json.Redo(); check(edits.IsDirty && graph.Connection(new(4, 0)).Destination == 2, "shared redo restores both new reciprocal pairs");
        throws(() => map.MoveExit(0, 4, 4), "paired exit edits cannot bypass link metadata transaction");
        links.MoveExit(0, 4, 4); var scene = new WorkspaceScene("A", jsonPath, map.SourcePath); var session = new WorkspaceSceneSession(root, scene);
        session.Save(json, map, links, edits);
        check(!edits.IsDirty && !map.IsDirty && File.ReadAllBytes(edits.TargetPath).SequenceEqual(edits.Serialize()), "workspace save commits map, links and new Levels override together");
        check(new PlacementLinks(PresetDocument.Load(jsonPath), Ds1CollisionDocument.Load(map.SourcePath)).Warning is null, "moved exits and authored routes reopen with valid paired metadata");
        check(File.ReadAllBytes(levelPath).SequenceEqual(Encoding.UTF8.GetBytes(levels)), "workspace connection authoring never modifies the base table");
        json.Undo(); json.Undo(); var diskMap = File.ReadAllBytes(map.SourcePath); var diskJson = File.ReadAllBytes(jsonPath); var diskLinks = File.ReadAllBytes(jsonPath + PlacementLinks.Suffix); var diskTable = File.ReadAllBytes(edits.TargetPath);
        using (var locked = new FileStream(edits.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            throws(() => session.Save(json, map, links, edits), "locked fourth file rejects combined scene save");
        check(File.ReadAllBytes(map.SourcePath).SequenceEqual(diskMap) && File.ReadAllBytes(jsonPath).SequenceEqual(diskJson) && File.ReadAllBytes(jsonPath + PlacementLinks.Suffix).SequenceEqual(diskLinks) && File.ReadAllBytes(edits.TargetPath).SequenceEqual(diskTable), "failed Levels replacement rolls back the previous three files");
        check(edits.IsDirty && map.IsDirty, "failed four-file save retains unsaved map and connection edits");
        session.Save(json, map, links, edits);
        // Unused-endpoint authoring and guard checks use independent snapshots.
        void Guard(string changedLevels, string changedPresets, string changedWarps, string label)
        {
            File.WriteAllText(edits.TargetPath, changedLevels); File.WriteAllText(presetPath, changedPresets); File.WriteAllText(warpPath, changedWarps);
            var test = new EntranceConnections(assets, root); throws(() => test.Plan(new(1, 0), new(3, 0)), label);
        }
        Guard(levels.Replace("\t0\t2\t2\t7", "\t0\t3\t2\t7"), presets, warps, "generated source area remains inspection only");
        Guard(levels.Replace("C\t3\t0", "C\t3\t1"), presets, warps, "cross-act transition authoring rejected");
        Guard(levels, presets.Replace("A\t1\t1\t1", "A\t1\t1\t0"), warps, "Scan disabled rejects transition authoring");
        Guard(levels, presets, warps + "Duplicate\t7\tl\t0\t0\n", "ambiguous direction-specific warp rejected");
        Guard(levels, presets.Replace("a.ds1", "missing.ds1"), warps, "unavailable preset variant rejects authoring");
        Guard(levels.Replace("B\t2\t0\t2\t1", "B\t2\t0\t2\t0"), presets, warps, "one-way existing route cannot be swapped silently");
        Guard(levels.Replace("2\t2\t7\t0\t7", "2\t2\t7\t2\t7"), presets, warps, "multiple source slots into one destination are ambiguous");
        string unused = levels; foreach (int value in new[] { 1, 2, 3, 4 }) unused = unused.Replace("2\t" + value + "\t7", "2\t0\t7");
        File.WriteAllText(edits.TargetPath, unused); File.WriteAllText(presetPath, presets); File.WriteAllText(warpPath, warps);
        var freshEdits = new LevelTableEdits(edits.TargetPath, root, json.History); var freshGraph = new EntranceConnections(assets, root, freshEdits);
        check(freshGraph.Plan(new(1, 0), new(3, 0)).Routes.Count == 2, "two unused supported endpoints form one reciprocal connection");
        freshGraph.Apply(new(1, 0), new(3, 0));
        check(freshGraph.Connection(new(1, 0)).Destination == 3 && freshGraph.Connection(new(3, 0)).Destination == 1, "new connection writes both Vis cells and retains Warp slots");
        File.AppendAllText(presetPath, "\n"); throws(freshEdits.VerifyUnchanged, "external preset dependency change blocks connection save"); File.WriteAllText(presetPath, presets);
        File.AppendAllText(map.SourcePath, "x"); throws(freshEdits.VerifyUnchanged, "external destination map change blocks connection save"); File.WriteAllBytes(map.SourcePath, original);
        File.AppendAllText(edits.TargetPath, "\n"); throws(freshEdits.VerifyUnchanged, "external Levels edit blocks staged patch save");
        string emptyWorkspace = Path.Combine(folder, "entrance-new-override"); Directory.CreateDirectory(Path.Combine(emptyWorkspace, "global", "excel"));
        File.WriteAllText(Path.Combine(baseRoot, "global", "excel", "lvlwarp.txt"), warps);
        var fallbackEdits = new LevelTableEdits(levelPath, emptyWorkspace, new EditHistory());
        _ = new EntranceConnections(assets, emptyWorkspace, fallbackEdits);
        File.WriteAllText(Path.Combine(emptyWorkspace, "global", "excel", "lvlwarp.txt"), warps);
        throws(fallbackEdits.VerifyUnchanged, "new workspace lookup override invalidates an observed base-table dependency");
    }
}
