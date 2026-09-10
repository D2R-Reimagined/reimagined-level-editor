using D2RLevel.Core;

/// <summary>
/// A link broken by an outside edit must be isolated: every other link keeps working,
/// the broken one refuses to move anything, and discarding it never touches map bytes.
/// </summary>
internal static class LinkRepairChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        static EntityTransform Shift(PresetEntity e, double x, double z = 0) =>
            e.Transform with { Position = e.Transform.Position with { X = e.Transform.Position.X + x, Z = e.Transform.Position.Z + z } };
        static bool Blocked(Ds1CollisionDocument d, int x, int y) => (d.Cell(d.Floors[0], x, y) & Ds1CollisionDocument.Unwalkable) != 0;

        // Two independently linked models, written out as a real pair.
        var (json, _, links) = LinkFixture.Create(folder);
        var first = json.Entities[0]; var second = json.Entities[1];
        links.LinkFootprint(first, [new(1, 1)], 10, false);
        links.LinkFootprint(second, [new(2, 2)], 10, false);
        links.LinkUnit(second, 1, 10);
        check(!links.HasBrokenLinks && links.LinkStates.Count == 2, "a freshly linked pair reports no broken links");
        string source = links.ExportPair(Path.Combine(folder, "repair-source"));

        // Reopen with the first footprint's blocking cleared by some other tool.
        var reopenedJson = PresetDocument.Load(source);
        var reopenedDs1 = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(source)!);
        reopenedDs1.Paint([(1, 1)], false);
        var repaired = new PlacementLinks(reopenedJson, reopenedDs1);
        var brokenEntity = reopenedJson.Entities[0]; var intactEntity = reopenedJson.Entities[1];
        check(repaired.Warning is null, "an externally changed footprint no longer disables the whole sidecar");
        check(repaired.BrokenLinkCount == 1 && repaired.BrokenReason(brokenEntity) is not null && repaired.BrokenReason(intactEntity) is null,
            "only the affected link is reported broken");

        // The intact neighbour must remain fully usable.
        repaired.Move(intactEntity, Shift(intactEntity, 10));
        check(reopenedDs1.Units[1].X == 17 && Blocked(reopenedDs1, 3, 2), "an intact link keeps moving while a sibling is broken");
        reopenedJson.Undo();

        throws(() => repaired.Move(brokenEntity, Shift(brokenEntity, 10)), "a broken link refuses to move its object");
        throws(() => repaired.AlignUnitToHd(brokenEntity), "a broken link refuses unit alignment");
        throws(() => repaired.LinkFootprint(brokenEntity, [new(0, 0)], 10, false), "a broken link refuses footprint edits");
        check(repaired.CollisionTiles(brokenEntity).Length == 0, "a broken link claims no collision tiles");

        // Its cells become editable again, and restoring the blocking heals the link.
        var beforeRepaint = reopenedDs1.Serialize();
        int painted = reopenedDs1.Paint([(1, 1)], true);
        check(painted > 0, "a broken link stops protecting its former footprint");
        check(!repaired.HasBrokenLinks && repaired.BrokenReason(brokenEntity) is null,
            "restoring the blocking outside the workspace heals the link");
        repaired.Move(brokenEntity, Shift(brokenEntity, 10));
        check(Blocked(reopenedDs1, 2, 1), "a healed link moves its collision again");
        reopenedJson.Undo(); reopenedDs1.Undo();
        check(reopenedDs1.Serialize().SequenceEqual(beforeRepaint) && repaired.BrokenLinkCount == 1,
            "undoing the outside repair restores both the bytes and the broken state");

        // Discarding drops the record and leaves every DS1 byte alone.
        var beforeDiscard = reopenedDs1.Serialize(); var jsonBeforeDiscard = reopenedJson.Serialize();
        int discarded = repaired.DiscardBrokenLinks();
        check(discarded == 1 && repaired.Links.Count == 1 && !repaired.HasBrokenLinks, "discarding removes only the broken link");
        check(reopenedDs1.Serialize().SequenceEqual(beforeDiscard) && reopenedJson.Serialize().SequenceEqual(jsonBeforeDiscard),
            "discarding a broken link changes neither map");
        check(repaired.Find(intactEntity) is not null, "discarding preserves intact links");
        reopenedJson.Undo();
        check(repaired.Links.Count == 2 && repaired.HasBrokenLinks, "discarding a broken link is undoable");
        reopenedJson.Redo();

        // A pair with a broken link still exports; the copy reports the same link broken.
        var (fj, fd, fl) = LinkFixture.Create(folder);
        fl.LinkFootprint(fj.Entities[0], [new(1, 1)], 10, false);
        fl.LinkFootprint(fj.Entities[1], [new(2, 2)], 10, false);
        string faithfulSource = fl.ExportPair(Path.Combine(folder, "faithful-source"));
        var faithfulDs1 = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(faithfulSource)!);
        faithfulDs1.Paint([(1, 1)], false);
        var faithful = new PlacementLinks(PresetDocument.Load(faithfulSource), faithfulDs1);
        check(faithful.BrokenLinkCount == 1, "the faithful-export fixture has one broken link");
        string carried = faithful.ExportPair(Path.Combine(folder, "faithful-export"));
        var carriedLinks = new PlacementLinks(PresetDocument.Load(carried), Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(carried)!));
        check(carriedLinks.Warning is null && carriedLinks.Links.Count == 2 && carriedLinks.BrokenLinkCount == 1,
            "exporting carries a broken link rather than stranding the rest of the pair");

        // The repaired pair still exports and reloads.
        repaired.SetCalibration(GridCalibration.Typed(10));
        string exported = repaired.ExportPair(Path.Combine(folder, "repaired-export"));
        var exportedLinks = new PlacementLinks(PresetDocument.Load(exported), Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(exported)!));
        check(exportedLinks.Warning is null && !exportedLinks.HasBrokenLinks && exportedLinks.Links.Count == 1,
            "a repaired pair exports and reloads clean");
        check(exportedLinks.Calibration.UnitsPerTile == 10 && exportedLinks.Calibration.Source == CalibrationSource.Manual,
            "an exported pair carries its calibration");

        // Selective discard leaves other broken links alone for separate review.
        var (mj, md, ml) = LinkFixture.Create(folder);
        ml.LinkFootprint(mj.Entities[0], [new(1, 1)], 10, false);
        ml.LinkFootprint(mj.Entities[1], [new(2, 2)], 10, false);
        ml.SaveMetadata();
        var bothDs1 = Ds1CollisionDocument.Load(md.SourcePath);
        bothDs1.Paint([(1, 1)], false); bothDs1.Paint([(2, 2)], false);
        var bothJson = PresetDocument.Load(mj.SourcePath);
        var both = new PlacementLinks(bothJson, bothDs1);
        check(both.BrokenLinkCount == 2, "independent outside edits break each link separately");
        both.DiscardBrokenLinks([bothJson.Entities[0].Id]);
        check(both.Links.Count == 1 && both.BrokenLinkCount == 1, "selective discard keeps the other broken link for review");
        check(both.DiscardBrokenLinks([bothJson.Entities[0].Id]) == 0, "discarding an already discarded link is a no-op");

        // A structural mismatch is still fatal: unit indices cannot be partially trusted.
        var (sj, sd, sl) = LinkFixture.Create(folder);
        sl.LinkUnit(sj.Entities[0], 0, 10);
        sl.SaveMetadata();
        string sidecar = sj.SourcePath + PlacementLinks.Suffix;
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace(sl.Links[0].EntityId, "999999"));
        var structural = new PlacementLinks(PresetDocument.Load(sj.SourcePath), Ds1CollisionDocument.Load(sd.SourcePath));
        check(structural.Warning is null && structural.BrokenLinkCount == 1, "a link naming a missing HD object is broken, not fatal");
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"Fingerprint\":", "\"Fingerprint\": \"tampered\", \"Ignored\":"));
        var tampered = new PlacementLinks(PresetDocument.Load(sj.SourcePath), Ds1CollisionDocument.Load(sd.SourcePath));
        check(tampered.Warning is not null, "a DS1 structure mismatch still disables the whole sidecar");
        throws(() => tampered.DiscardBrokenLinks(), "a disabled sidecar cannot be partially repaired");
    }
}
