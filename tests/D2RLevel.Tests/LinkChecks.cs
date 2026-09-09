using D2RLevel.Core;

internal static class LinkChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        (PresetDocument Json, Ds1CollisionDocument Ds1, PlacementLinks Links) Fixture(bool sharedAnchor = false)
        {
            string root = Path.Combine(folder, "links-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string map = Path.Combine(root, "town.ds1"), preset = Path.Combine(root, "town.json");
            using (var w = new BinaryWriter(File.Create(map)))
            {
                foreach (int n in new[] { 18, 3, 3, 0, 2, 0, 1, 1 }) w.Write(n);
                w.Write(new byte[16 * 4 * 2]); // walls and orientations
                for (int i = 0; i < 16; i++) w.Write(1); // nonempty floor
                w.Write(new byte[16 * 4 * 2]); // shadow and tags
                w.Write(2);
                foreach (int n in new[] { 1, 7, 5, 6, 123, 2, 9, sharedAnchor ? 5 : 12, sharedAnchor ? 6 : 12, 456 }) w.Write(n);
                w.Write(0); w.Write(0); // v18 unknown and groups
                foreach (int n in new[] { 1, 2, 5, 6, 6, 7, 42, 8, 9, 77 }) w.Write(n);
            }
            File.WriteAllText(preset, "{\"entities\":[]}");
            var seed = PresetDocument.Load(preset);
            seed.AddModel("data/hd/a.model", new(10, 0, 10)); seed.AddModel("data/hd/b.model", new(10, 0, 10));
            File.WriteAllBytes(preset, seed.Serialize());
            var json = PresetDocument.Load(preset); var ds1 = Ds1CollisionDocument.Load(map);
            return (json, ds1, new(json, ds1));
        }
        EntityTransform Shift(PresetEntity e, double x, double z = 0) => e.Transform with { Position = e.Transform.Position with { X = e.Transform.Position.X + x, Z = e.Transform.Position.Z + z } };
        bool Blocked(Ds1CollisionDocument d, int x, int y) => (d.Cell(d.Floors[0], x, y) & Ds1CollisionDocument.Unwalkable) != 0;
        {
            var (dj, dd, dl) = Fixture(); var first = dj.Entities[0]; var second = dj.Entities[1];
            dd.Paint([(0, 0)], true);
            dl.LinkFootprint(first, [new(0, 0), new(1, 1), new(2, 1)], 10, false);
            dl.LinkFootprint(second, [new(2, 1)], 10, false);
            dl.LinkUnit(first, 0, 10); dl.LinkUnit(second, 1, 10);
            var jsonBefore = dj.Serialize(); var ds1Before = dd.Serialize();
            dl.DeleteModel(first);
            check(dj.Entities.Count == 1 && !Blocked(dd, 1, 1) && Blocked(dd, 0, 0) && Blocked(dd, 2, 1), "delete clears owned collision but keeps overlap and protected baseline");
            check(dd.Units.Count == 1 && dd.Units[0].Id == 9 && dd.PatrolPoints(0).Count == 0 && dl.Find(second)!.Unit!.Index == 0, "delete removes linked unit and patrol and remaps surviving link indices");
            dd.Undo();
            check(dj.Serialize().SequenceEqual(jsonBefore) && dd.Serialize().SequenceEqual(ds1Before) && dl.Find(first) is not null, "one DS1 undo restores exact model unit patrol collision and links");
            dj.Redo();
            string deletedExport = dl.ExportPair(Path.Combine(folder, "deleted-export"));
            var exportedJson = PresetDocument.Load(deletedExport);
            var exportedDs1 = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(deletedExport)!);
            var exportedLinks = new PlacementLinks(exportedJson, exportedDs1);
            check(exportedLinks.Warning is null && exportedJson.Entities.Count == 1 && exportedDs1.Units.Count == 1, "deleted pair exports and reloads with valid fingerprint");
            dl.Move(second, Shift(second, 2)); check(dd.Units[0].X == 13, "surviving linked unit moves after deletion");
            dj.Undo(); dj.Undo(); check(dd.Serialize().SequenceEqual(ds1Before), "undo deletion after export remains lossless");
            var (sj, sd, sl) = Fixture(true); sl.LinkUnit(sj.Entities[0], 0, 10);
            sl.DeleteModel(sj.Entities[0]);
            check(sd.PatrolPoints(0).Count == 2, "deleting unit preserves patrol shared by surviving anchor");
            var (uj, ud, ul) = Fixture(); var unchanged = ud.Serialize(); ul.DeleteModel(uj.Entities[0]);
            check(ud.Serialize().SequenceEqual(unchanged) && uj.Entities.Count == 1, "unlinked model deletion leaves DS1 bytes unchanged");
        }
        {
            var (gj, gd, gl) = Fixture(); var a = gj.Entities[0]; var bGroup = gj.Entities[1];
            var stationary = gj.AddModel("data/hd/stationary.model", new(10,0,10));
            gl.LinkFootprint(a, [new(0,1),new(1,1)],10,false);
            gl.LinkFootprint(bGroup, [new(1,1)],10,false);
            gl.LinkFootprint(stationary, [new(0,1)],10,false);
            gl.LinkUnit(a,0,10); gl.LinkUnit(bGroup,1,10);
            var jsonBefore = gj.Serialize(); var dsBefore = gd.Serialize(); var transforms = new[] { a.Transform, bGroup.Transform };
            GroupMovement.Move(gj,gl,[a,bGroup],new(10,0,0));
            check(Blocked(gd,0,1) && Blocked(gd,1,1) && Blocked(gd,2,1), "group move preserves stationary overlap and translates shared collision");
            check(gd.Units[0].X == 10 && gd.Units[1].X == 17 && gd.PatrolPoints(0)[0].X == 11, "group move translates both linked units and patrol");
            gj.Undo();
            check(gj.Serialize().SequenceEqual(jsonBefore) && gd.Serialize().SequenceEqual(dsBefore), "one undo restores whole group JSON collision units and patrol");
            gj.Redo(); gj.Undo();
            gl.LinkFootprint(bGroup,[new(3,1)],10,false);
            jsonBefore = gj.Serialize(); dsBefore = gd.Serialize();
            throws(() => GroupMovement.Move(gj,gl,[a,bGroup],new(10,0,0)), "invalid second member rejects entire group move");
            check(gj.Serialize().SequenceEqual(jsonBefore) && gd.Serialize().SequenceEqual(dsBefore), "late group failure rolls back prior members and collision");
            gl.Move(a, Shift(a,10)); gj.Undo();
            check(gd.Serialize().SequenceEqual(dsBefore), "group rollback leaves ownership usable for later moves");
            var groups = new AssetGroups(gj); groups.Create("Well decorations",[a,bGroup]);
            check(gj.Serialize().SequenceEqual(jsonBefore) && new AssetGroups(gj).Resolve(new AssetGroups(gj).Find(a)!).Length == 2, "saved named group reloads without editing game JSON");
            groups.Remove([a]); check(new AssetGroups(gj).Find(bGroup) is null, "ungroup removes persisted membership");
            File.WriteAllText(groups.Path,"broken"); var corrupt = new AssetGroups(gj);
            throws(() => corrupt.Create("Replacement",[a,bGroup]), "corrupt group metadata cannot be silently overwritten");
        }
        var (j, d, links) = Fixture(); var e = j.Entities[0]; var start = e.Transform; var original = d.Serialize();
        links.LinkUnit(e, 0, 10); links.SaveMetadata();
        check(new PlacementLinks(PresetDocument.Load(j.SourcePath), Ds1CollisionDocument.Load(d.SourcePath)).Warning is null, "saved unit links reopen without reassignment");
        check(PlacementLinks.LinkedDs1Path(j.SourcePath) == d.SourcePath, "sidecar resolves relative DS1 path");
        links.Move(e, Shift(e, 4, 2));
        check(d.Units[0].X == 7 && d.Units[0].Y == 7 && d.PatrolPoints(0)[0] == new Ds1PathPoint(8, 8, 42), "HD movement translates linked DS1 unit and patrol");
        d.Undo(); check(e.Transform == start && d.Serialize().SequenceEqual(original), "DS1 undo restores both documents as one edit");
        j.Redo(); check(e.Transform != start && d.Units[0].X == 7, "JSON redo restores both linked documents");
        throws(() => d.MoveUnit(0, 8, 8), "direct linked unit movement is protected");
        throws(() => d.DiscardChanges(), "cannot discard only half a shared workspace");
        var before = d.Serialize(); var transform = e.Transform;
        throws(() => links.Move(e, Shift(e, 1000)), "out of bounds linked move rejected");
        check(before.SequenceEqual(d.Serialize()) && e.Transform == transform, "rejected linked move changes neither document");
        throws(() => links.Move(e, e.Transform with { Scale = new(2, 2, 2) }), "linked scale change requires relinking");
        j.Undo(); links.Move(e, Shift(e, .4)); links.Move(e, Shift(e, .4)); links.Move(e, Shift(e, .4));
        check(d.Units[0].X == 6, "small moves accumulate against original anchor without rounding drift");
        var output = links.ExportPair(folder); var reJ = PresetDocument.Load(output); var reD = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(output)!);
        var reLinks = new PlacementLinks(reJ, reD);
        check(reLinks.Warning is null && reLinks.HasLinks && reJ.Entities[0].Transform == e.Transform && reD.Serialize().SequenceEqual(d.Serialize()), "linked export reloads JSON DS1 and ownership metadata together");
        check(File.ReadAllBytes(d.SourcePath).SequenceEqual(original) && !j.IsDirty && !d.IsDirty && !links.HasMetadataChanges, "paired export preserves sources and marks saved workspace clean");
        reLinks.Move(reJ.Entities[0], Shift(reJ.Entities[0], 2)); check(reD.Units[0].X == 7, "reopened links continue moving units");
        File.WriteAllText(output + PlacementLinks.Suffix, "broken");
        var broken = new PlacementLinks(reJ, reD); check(broken.Warning is not null, "corrupt sidecar disables linkage explicitly");
        throws(() => broken.Move(reJ.Entities[0], Shift(reJ.Entities[0], 2)), "corrupt links cannot silently become HD only moves");

        (j, d, links) = Fixture(); e = j.Entities[0]; var b = j.Entities[1];
        d.Paint([(1, 1)], true);
        links.LinkFootprint(e, [new(1, 1)], 10, false); links.Move(e, Shift(e, 10));
        check(Blocked(d, 1, 1) && Blocked(d, 2, 1), "unclaimed existing floor blocking preserved");
        d.Undo(); d.Undo(); // move and link
        links.LinkFootprint(e, [new(1, 1)], 10, true);
        links.LinkFootprint(b, [new(1, 1)], 10, true);
        links.Move(e, Shift(e, 10));
        check(Blocked(d, 1, 1) && Blocked(d, 2, 1), "overlapping owner keeps shared collision blocked");
        links.Move(b, Shift(b, 10, 10));
        check(!Blocked(d, 1, 1) && Blocked(d, 2, 1) && Blocked(d, 2, 2), "last departing owner clears only claimed override");
        throws(() => d.Paint([(2, 1)], false), "owned footprint protected from direct painting");
        before = d.Serialize(); transform = e.Transform;
        throws(() => links.Move(e, Shift(e, 100)), "out of bounds footprint rejected atomically");
        check(before.SequenceEqual(d.Serialize()) && e.Transform == transform, "invalid footprint leaves both documents unchanged");
        var exported = links.ExportPair(folder);
        var fJ = PresetDocument.Load(exported); var fD = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(exported)!); var fLinks = new PlacementLinks(fJ, fD);
        check(fLinks.Warning is null && fLinks.Links.Count == 2, "footprint baselines survive paired export");
        fLinks.Move(fJ.Entities[0], Shift(fJ.Entities[0], -10));
        check(!Blocked(fD, 2, 1) && Blocked(fD, 1, 1), "reopened footprint restores destination baseline on departure");
        links.Unlink(e); links.Move(b, Shift(b, 0, -10)); links.Move(b, Shift(b, -10));
        check(Blocked(d, 2, 1), "unlinked stationary collision protected from remaining moving owner");

        (j, d, links) = Fixture(); e = j.Entities[0]; start = e.Transform;
        j.SetTransform(e, Shift(e, 2)); d.Paint([(0, 0)], true); links.LinkUnit(e, 0, 10);
        j.Undo(); j.Undo(); check(!Blocked(d, 0, 0) && e.Transform != start, "first link merges earlier independent edits in chronological order");
        d.Undo(); check(e.Transform == start, "merged history can undo original HD edit from DS1");
        d.Redo(); d.Redo(); d.Redo(); check(links.HasLinks && Blocked(d, 0, 0), "merged history redo restores edits and link");
        links.SaveMetadata();
        var stale = new PlacementLinks(PresetDocument.Load(j.SourcePath), Ds1CollisionDocument.Load(d.SourcePath));
        check(stale.Warning is not null, "stale source pair is detected instead of guessing placement");

        (j, d, links) = Fixture(); e = j.Entities[0]; b = j.Entities[1];
        links.LinkUnit(e, 0, 10); links.Move(e, Shift(e, 2));
        var unitBefore = d.Units[0];
        links.LinkFootprint(e, [new(1, 1)], 10, true);
        check(links.Find(e) is { Unit: not null, Tiles.Length: 1 } && d.Units[0] == unitBefore, "attach collision to an already moved unit link without shifting unit");
        links.LinkFootprint(b, [new(1, 1)], 10, true);
        links.Move(e, Shift(e, 10));
        check(d.Units[0].X == unitBefore.X + 5 && Blocked(d, 1, 1) && Blocked(d, 2, 1), "combined unit and collision move preserves overlapping owner");
        d.Undo(); check(d.Units[0] == unitBefore && !Blocked(d, 2, 1), "combined move undo restores unit patrol and footprint");
        d.Redo();
        links.LinkFootprint(e, [new(1, 1), new(2, 2)], 10, false);
        check(!Blocked(d, 2, 1) && Blocked(d, 1, 1) && Blocked(d, 2, 2) && links.Find(e)?.Unit is not null, "reshape moved footprint releases old cells and keeps unit link");
        var ownerInfo = links.InspectOwnership(new(1, 1));
        check(ownerInfo.Owners.Length == 2 && !ownerInfo.ProtectedBlocking, "ownership inspection distinguishes shared owners from static baseline");
        before = d.Serialize(); links.LinkFootprint(e, [new(2, 2), new(1, 1)], 10, false);
        d.Undo(); check(Blocked(d, 2, 1) && !Blocked(d, 2, 2), "unchanged footprint is a no-op and does not add undo entry");
        d.Redo();
        throws(() => links.LinkFootprint(e, [new(1, 1)], 20, false), "reject collision scale mismatch with existing unit anchor");
        check(before.SequenceEqual(d.Serialize()), "rejected reshape preserves unit collision and metadata state");
        links.LinkFootprint(e, [], 10, false);
        check(links.Find(e) is { Unit: not null, Tiles.Length: 0 } && Blocked(d, 1, 1) && !Blocked(d, 2, 2), "remove only selected collision preserves shared cells and unit link");
        d.Undo(); check(links.Find(e)?.Tiles.Length == 2 && Blocked(d, 2, 2), "undo collision removal restores ownership and bytes");
        var combinedExport = links.ExportPair(folder);
        var cJ = PresetDocument.Load(combinedExport); var cD = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(combinedExport)!);
        var cLinks = new PlacementLinks(cJ, cD);
        check(cLinks.Warning is null && cLinks.Find(cJ.Entities[0]) is { Unit: not null, Tiles.Length: 2 }, "combined links and reshaped footprints persist across reload");

        (j, d, links) = Fixture(); e = j.Entities[0];
        links.LinkFootprint(e, [new(1, 1)], 10, false); links.Move(e, Shift(e, 10));
        links.LinkUnit(e, 0, 10); unitBefore = d.Units[0]; links.Move(e, Shift(e, -10));
        check(d.Units[0].X == unitBefore.X - 5 && Blocked(d, 1, 1) && !Blocked(d, 2, 1), "adding a unit to moved collision preserves common anchor");

        (j, d, links) = Fixture(); e = j.Entities[0]; b = j.Entities[1];
        d.Paint([(0, 0)], true);
        links.LinkFootprint(e, [new(1, 1)], 10, false); links.LinkFootprint(b, [new(1, 1)], 10, false);
        var random = new Random(715); var positions = new[] { (X: 1, Y: 1), (X: 1, Y: 1) };
        bool valid = true;
        bool Matches()
        {
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                if (Blocked(d, x, y) != ((x == 0 && y == 0) || positions.Any(p => p == (x, y)))) return false;
            return true;
        }
        for (int step = 0; step < 300; step++)
        {
            int who = random.Next(2); var old = positions[who]; var next = (X: random.Next(4), Y: random.Next(4));
            if (old == next) continue;
            var model = j.Entities[who]; links.Move(model, model.Transform with { Position = new(next.X * 10, 0, next.Y * 10) });
            positions[who] = next; valid &= Matches();
            if (step % 7 == 0)
            {
                d.Undo(); positions[who] = old; valid &= Matches();
                j.Redo(); positions[who] = next; valid &= Matches();
            }
        }
        check(valid, "300 randomized overlapping moves and cross-view undo redo preserve union of owners plus static baseline");
        links.LinkFootprint(e, [], 10, false); positions[0] = (-1, -1);
        links.LinkFootprint(b, [], 10, false); positions[1] = (-1, -1);
        check(Matches() && !links.HasLinks && Blocked(d, 0, 0), "removing all owners leaves only original protected collision");
        (j, d, links) = Fixture(); e = j.Entities[0];
        links.LinkUnit(e, 0, 10); links.LinkFootprint(e, [new(1, 1)], 10, false);
        var unaligned = d.Serialize(); var hdBefore = e.Transform;
        links.AlignUnitToHd(e);
        check(d.Units[0] is { X: 5, Y: 5 } && e.Transform == hdBefore && Blocked(d, 1, 1), "explicit alignment places unit at HD origin without moving HD or collision");
        d.Undo(); check(d.Serialize().SequenceEqual(unaligned), "alignment undo restores exact unit and patrol bytes");
        d.Redo(); links.Move(e, Shift(e, 10));
        check(d.Units[0] is { X: 10, Y: 5 } && Blocked(d, 2, 1), "aligned unit and footprint continue moving together");
        var alignedExport = links.ExportPair(folder); var aJ = PresetDocument.Load(alignedExport);
        var aD = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(alignedExport)!);
        check(new PlacementLinks(aJ, aD).Warning is null, "alignment anchor survives paired export and reload");

        (j, d, links) = Fixture();
        string workspaceRoot = Path.Combine(folder, "workspace", "data");
        string workspaceJson = Path.Combine(workspaceRoot, "hd", "env", "preset", "act1", "town", "town.json");
        string workspaceDs1 = Path.Combine(workspaceRoot, "global", "tiles", "act1", "town", "town.ds1");
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceJson)!); Directory.CreateDirectory(Path.GetDirectoryName(workspaceDs1)!);
        File.Copy(j.SourcePath, workspaceJson); File.Copy(d.SourcePath, workspaceDs1);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspaceJson)!, "unmatched.json"), "{}");
        var scenes = SceneWorkspace.Scan(workspaceRoot);
        check(scenes.Length == 1 && scenes[0].Name == "act1/town/town.json", "workspace discovers only matching local scene pairs");
        throws(() => SceneWorkspace.Inside(workspaceRoot, Path.Combine(workspaceRoot, "..", "external.ds1")), "workspace rejects paths outside selected data directory");
        using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); throws(() => SceneWorkspace.Scan(workspaceRoot, cancel.Token), "workspace scan observes cancellation"); }
        var session = new WorkspaceSceneSession(workspaceRoot, scenes[0]);
        j = PresetDocument.Load(workspaceJson); d = Ds1CollisionDocument.Load(workspaceDs1); links = new(j, d); links.ConnectWorkspace();
        var sourceJsonBytes = File.ReadAllBytes(workspaceJson); var sourceDs1Bytes = File.ReadAllBytes(workspaceDs1);
        e = j.Entities[0]; links.LinkUnit(e, 0, 10); links.LinkFootprint(e, [new(1, 1)], 10, false); links.Move(e, Shift(e, 10));
        session.Save(j, d, links);
        check(File.ReadAllBytes(workspaceJson).SequenceEqual(j.Serialize()) && File.ReadAllBytes(workspaceDs1).SequenceEqual(d.Serialize()) && File.Exists(workspaceJson + PlacementLinks.Suffix), "Save Scene writes both original locations and link metadata");
        check(File.ReadAllBytes(workspaceJson + ".bak").SequenceEqual(sourceJsonBytes) && File.ReadAllBytes(workspaceDs1 + ".bak").SequenceEqual(sourceDs1Bytes), "Save Scene preserves previous maps as backups");
        check(!j.IsDirty && !d.IsDirty && !links.HasMetadataChanges && SceneWorkspace.Scan(workspaceRoot).Length == 1, "workspace save marks both documents clean and excludes sidecars from browser");
        j.Undo(); check(j.IsDirty && d.IsDirty, "undo after direct scene save marks both documents dirty");
        session.Save(j, d, links);
        var onDiskJson = File.ReadAllBytes(workspaceJson); var onDiskDs1 = File.ReadAllBytes(workspaceDs1); var onDiskLinks = File.ReadAllBytes(workspaceJson + PlacementLinks.Suffix);
        links.Move(e, Shift(e, 2));
        File.AppendAllText(workspaceJson, " ");
        throws(() => session.Save(j, d, links), "Save Scene rejects external file changes");
        check(File.ReadAllBytes(workspaceDs1).SequenceEqual(onDiskDs1), "external edit conflict does not partially save DS1");
        File.WriteAllBytes(workspaceJson, onDiskJson);
        using (var held = new FileStream(workspaceDs1, FileMode.Open, FileAccess.Read, FileShare.Read))
            throws(() => session.Save(j, d, links), "locked DS1 rejects scene save after JSON replacement");
        check(File.ReadAllBytes(workspaceJson).SequenceEqual(onDiskJson) && File.ReadAllBytes(workspaceDs1).SequenceEqual(onDiskDs1) && j.IsDirty, "failed second write restores JSON and retains unsaved editor changes");
        using (var held = new FileStream(workspaceJson + PlacementLinks.Suffix, FileMode.Open, FileAccess.Read, FileShare.Read))
            throws(() => session.Save(j, d, links), "locked sidecar rejects third write");
        check(File.ReadAllBytes(workspaceJson).SequenceEqual(onDiskJson) && File.ReadAllBytes(workspaceDs1).SequenceEqual(onDiskDs1) && File.ReadAllBytes(workspaceJson + PlacementLinks.Suffix).SequenceEqual(onDiskLinks), "failed third write restores entire original scene pair");
        session.Save(j, d, links);
        check(new PlacementLinks(PresetDocument.Load(workspaceJson), Ds1CollisionDocument.Load(workspaceDs1)).Warning is null, "direct saved scene reopens with valid links");
        string settingsFile = Path.Combine(folder, "workspace-settings.json");
        new EditorSettings(AssetFolder: "outside-assets", WorkspaceFolder: workspaceRoot, RecentWorkspaces: [workspaceRoot]).Save(settingsFile);
        var savedSettings = EditorSettings.Load(settingsFile, out _);
        check(savedSettings.WorkspaceFolder == workspaceRoot && savedSettings.AssetFolder == "outside-assets" && savedSettings.RecentWorkspaces?.Length == 1, "workspace history and independent asset folder survive settings reload");
    }
}
