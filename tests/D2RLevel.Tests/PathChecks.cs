using D2RLevel.Core;

/// <summary>
/// Patrol path editing. Point moves are byte patches; inserts and removals splice the
/// record and re-read the block, so every case is checked for exact undo and for leaving
/// unrelated bytes alone.
/// </summary>
internal static class PathChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        // The shared fixture's unit 0 sits at (5,6) and owns a two-point path.
        static (PresetDocument Json, Ds1CollisionDocument Ds1) Fixture(string folder, bool sharedAnchor = false)
        {
            var (json, ds1, _) = LinkFixture.Create(folder, sharedAnchor);
            return (json, ds1);
        }
        static string Points(Ds1CollisionDocument d, int unit) =>
            string.Join(" ", d.PatrolPoints(unit).Select(p => $"({p.X},{p.Y}):{p.Action}"));

        var (_, ds1) = Fixture(folder);
        check(ds1.GameplayWarning is null && ds1.Units.Count == 2, "path fixture parses its gameplay block");
        check(Points(ds1, 0) == "(6,7):42 (8,9):77", "unit 0 owns the fixture's two-point path");
        check(ds1.PatrolPoints(1).Count == 0, "unit 1 has no patrol path");
        check(ds1.PathEditWarning(0) is null && ds1.PathEditWarning(1) is null, "both fixture units accept path editing");

        // Moving a point patches coordinates in place.
        var original = ds1.Serialize();
        ds1.MovePathPoint(0, 1, 11, 12);
        check(Points(ds1, 0) == "(6,7):42 (11,12):77", "moving a point rewrites only that point");
        check(ds1.Serialize().Length == original.Length, "moving a point does not resize the DS1");
        ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original), "move undo restores the exact bytes");
        ds1.Redo(); ds1.Undo();

        ds1.MovePathPoint(0, 0, 6, 7);
        check(ds1.Serialize().SequenceEqual(original) && !ds1.CanUndo, "moving a point to where it already is records nothing");
        throws(() => ds1.MovePathPoint(0, 0, -1, 4), "reject a patrol point outside the grid");
        throws(() => ds1.MovePathPoint(0, 0, 20, 0), "reject a patrol point past the subtile width");
        throws(() => ds1.MovePathPoint(0, 5, 4, 4), "reject an out-of-range point index");
        throws(() => ds1.MovePathPoint(1, 0, 4, 4), "reject moving a point on a unit with no path");
        check(ds1.Serialize().SequenceEqual(original), "every rejected move leaves the DS1 untouched");

        // Actions are patched independently of coordinates.
        ds1.SetPathPointAction(0, 0, 3);
        check(Points(ds1, 0) == "(6,7):3 (8,9):77", "the action of one point changes alone");
        ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original), "action undo restores the exact bytes");

        // Inserting in the middle grows the record.
        ds1.InsertPathPoint(0, 1, 7, 7, 2);
        check(Points(ds1, 0) == "(6,7):42 (7,7):2 (8,9):77", "a point inserts at the requested index");
        check(ds1.Serialize().Length == original.Length + 12, "an inserted point grows the DS1 by one record");
        ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original), "insert undo restores the exact bytes");
        ds1.Redo();
        check(Points(ds1, 0) == "(6,7):42 (7,7):2 (8,9):77", "insert redo restores the point");
        ds1.Undo();

        ds1.InsertPathPoint(0, 2, 15, 16, 6);
        check(Points(ds1, 0) == "(6,7):42 (8,9):77 (15,16):6", "a point appends at the end of the path");
        ds1.Undo();
        ds1.InsertPathPoint(0, 0, 1, 1, 1);
        check(Points(ds1, 0) == "(1,1):1 (6,7):42 (8,9):77", "a point inserts before the first one");
        ds1.Undo();
        throws(() => ds1.InsertPathPoint(0, 3, 4, 4, 1), "reject an insert index past the end");
        throws(() => ds1.InsertPathPoint(0, 0, 99, 0, 1), "reject inserting a point outside the grid");
        check(ds1.Serialize().SequenceEqual(original), "rejected inserts leave the DS1 untouched");

        // Removing shrinks the record, and removing the last point drops it entirely.
        ds1.RemovePathPoint(0, 0);
        check(Points(ds1, 0) == "(8,9):77", "removing a point keeps the rest of the path");
        check(ds1.Serialize().Length == original.Length - 12, "a removed point shrinks the DS1 by one record");
        ds1.RemovePathPoint(0, 0);
        check(ds1.PatrolPoints(0).Count == 0, "removing the last point drops the path");
        check(ds1.Serialize().Length == original.Length - 12 - 24, "dropping a path also removes its header");
        ds1.Undo(); ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original), "two removals undo back to the exact original bytes");

        // A unit with no path gets one from the first insert.
        var (_, fresh) = Fixture(folder);
        var freshOriginal = fresh.Serialize();
        fresh.InsertPathPoint(1, 0, 3, 4, 1);
        check(Points(fresh, 1) == "(3,4):1", "the first insert creates a path for a unit that had none");
        check(fresh.PatrolPoints(0).Count == 2, "creating a path leaves the other unit's path intact");
        fresh.InsertPathPoint(1, 1, 6, 7, 2);
        check(Points(fresh, 1) == "(3,4):1 (6,7):2" && Points(fresh, 0) == "(6,7):42 (8,9):77",
            "a created path extends without disturbing the existing one");
        fresh.Undo(); fresh.Undo();
        check(fresh.Serialize().SequenceEqual(freshOriginal), "creating and extending a path undoes exactly");

        // A created path survives a save and reload.
        fresh.Redo(); fresh.Redo();
        string saved = Path.Combine(folder, "paths-" + Guid.NewGuid().ToString("N") + ".ds1");
        fresh.SaveCopy(saved);
        var reloaded = Ds1CollisionDocument.Load(saved);
        check(reloaded.GameplayWarning is null && Points(reloaded, 1) == "(3,4):1 (6,7):2" && Points(reloaded, 0) == "(6,7):42 (8,9):77",
            "a created path reopens with both paths intact");
        check(reloaded.Units.Count == 2 && reloaded.Units[0].Id == 7 && reloaded.Units[1].Id == 9,
            "path edits leave unit records untouched");

        // Moving the unit still translates the path it now owns.
        fresh.MoveUnit(1, 13, 13);
        check(Points(fresh, 1) == "(4,5):1 (7,8):2", "a created path translates with its unit");
        fresh.Undo();
        check(Points(fresh, 1) == "(3,4):1 (6,7):2", "translating a created path undoes exactly");

        // Ambiguous anchors are refused rather than guessed at.
        var (_, shared) = Fixture(folder, sharedAnchor: true);
        check(shared.PathEditWarning(1) is not null, "a unit sharing another's position cannot own path edits");
        throws(() => shared.InsertPathPoint(1, 0, 4, 4, 1), "reject creating a path on an ambiguous anchor");

        // Path edits must not invalidate placement links, which never reference a patrol.
        var (lj, ld, ll) = LinkFixture.Create(folder);
        ll.LinkUnit(lj.Entities[0], 0, 10);
        ll.LinkFootprint(lj.Entities[1], [new(1, 1)], 10, false);
        string exported = ll.ExportPair(Path.Combine(folder, "path-links"));
        var pj = PresetDocument.Load(exported);
        var pd = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(exported)!);
        var pl = new PlacementLinks(pj, pd);
        check(pl.Warning is null && !pl.HasBrokenLinks, "the linked path fixture reloads clean");
        pd.MovePathPoint(0, 0, 4, 4);
        check(pl.Warning is null && !pl.HasBrokenLinks, "moving a patrol point keeps every link intact");
        pd.SetPathPointAction(0, 0, 5);
        check(!pl.HasBrokenLinks, "changing a patrol action keeps every link intact");
        pd.InsertPathPoint(0, 0, 6, 6, 1);
        check(!pl.HasBrokenLinks, "inserting a patrol point keeps every link intact");
        pd.RemovePathPoint(0, 0);
        check(!pl.HasBrokenLinks, "removing a patrol point keeps every link intact");
        // The fingerprint excludes the patrol block, so a reload still validates.
        string afterPaths = pl.ExportPair(Path.Combine(folder, "path-links-edited"));
        var rj = PresetDocument.Load(afterPaths);
        var rd = Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(afterPaths)!);
        var rl = new PlacementLinks(rj, rd);
        check(rl.Warning is null && !rl.HasBrokenLinks && rl.Links.Count == 2,
            "a pair whose patrol was inserted into and shortened reloads with links intact");

        // A version 1 sidecar keeps validating against its own fingerprint form.
        var (vj, vd, vl) = LinkFixture.Create(folder);
        vl.LinkUnit(vj.Entities[0], 0, 10);
        vl.SaveMetadata();
        string sidecar = vj.SourcePath + PlacementLinks.Suffix;
        string text = File.ReadAllText(sidecar);
        File.WriteAllText(sidecar, text.Replace("\"Version\": 2", "\"Version\": 1")
            .Replace(vd.LinkFingerprint(), vd.LegacyLinkFingerprint()));
        var legacy = new PlacementLinks(PresetDocument.Load(vj.SourcePath), Ds1CollisionDocument.Load(vd.SourcePath));
        check(legacy.Warning is null && legacy.HasLinks, "a version 1 sidecar validates against the legacy fingerprint");
        legacy.SaveMetadata();
        var upgraded = new PlacementLinks(PresetDocument.Load(vj.SourcePath), Ds1CollisionDocument.Load(vd.SourcePath));
        check(upgraded.Warning is null && upgraded.HasLinks, "the upgraded sidecar validates against the current fingerprint");
    }
}
