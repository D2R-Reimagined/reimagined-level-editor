using D2RLevel.Core;

internal static class GameplayChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string Fixture(int version, bool shared = false, bool truncate = false)
        {
            string path = Path.Combine(folder, $"units-{version}-{shared}-{truncate}.ds1");
            using var w = new BinaryWriter(File.Create(path));
            w.Write(version); w.Write(3); w.Write(3); w.Write(0); w.Write(2); w.Write(0); w.Write(1); w.Write(1);
            w.Write(new byte[16 * 4 * 5]); // wall, orientation, floor, shadow, tag
            w.Write(2);
            foreach (int n in new[] { 1, 7, 5, 6, 123, 2, 9, shared ? 5 : 12, shared ? 6 : 12, 456 }) w.Write(n);
            if (version >= 18) w.Write(0x12345678);
            w.Write(1); foreach (int n in new[] { 1, 2, 3, 4, 999 }) w.Write(n); // group
            w.Write(1); w.Write(2); w.Write(5); w.Write(6); // path and anchor
            w.Write(6); w.Write(7); w.Write(42);
            if (!truncate) { w.Write(8); w.Write(9); w.Write(77); w.Write(new byte[] { 0xab, 0xcd }); }
            return path;
        }
        foreach (int version in new[] { 16, 17, 18 })
        {
            string source = Fixture(version); var doc = Ds1CollisionDocument.Load(source); var original = doc.Serialize();
            check(doc.GameplayWarning is null && doc.Units.Count == 2 && doc.PatrolPoints(0).Count == 2, $"v{version} gameplay sections and group preamble");
            doc.MoveUnit(0, 7, 8);
            check(doc.Units[0] is { X: 7, Y: 8, Id: 7, Flags: 123 } && doc.PatrolPoints(0).SequenceEqual(new[] { new Ds1PathPoint(8, 9, 42), new Ds1PathPoint(10, 11, 77) }), $"v{version} unit move translates linked patrol and preserves actions");
            var changed = doc.Serialize(); doc.Undo();
            check(doc.Serialize().SequenceEqual(original) && !doc.CanUndo, "unit/path move is one lossless undo");
            doc.Redo(); string copy = Path.Combine(folder, $"units-copy-{version}.ds1"); doc.SaveCopy(copy);
            check(Ds1CollisionDocument.Load(copy).Serialize().SequenceEqual(changed) && File.ReadAllBytes(source).SequenceEqual(original) && doc.SameUneditedData(Ds1CollisionDocument.Load(source)), "unit save/reopen preserves source and compatible map context");
            throws(() => doc.MoveUnit(0, 19, 19), "reject translated patrol outside map");
            throws(() => doc.MoveUnit(0, 12, 12), "reject moving patrol onto another unit");
            throws(() => doc.MoveUnit(1, 7, 8), "reject accidental patrol reassignment");
            check(doc.Serialize().SequenceEqual(changed), "failed gameplay moves are atomic");
            doc.Undo(); doc.MoveUnit(1, 13, 12);
            check(!doc.CanRedo && doc.PatrolPoints(0)[0] == new Ds1PathPoint(6, 7, 42), "new unit move clears redo and leaves unrelated patrol intact");
            doc.DiscardChanges();
            check(!doc.IsDirty && !doc.CanUndo && !doc.CanRedo && doc.Serialize().SequenceEqual(changed), "discard restores last saved shared DS1 and clears history");
        }
        var shared = Ds1CollisionDocument.Load(Fixture(18, shared: true));
        throws(() => shared.MoveUnit(0, 7, 8), "shared spawn anchor cannot silently steal patrol");
        var broken = Ds1CollisionDocument.Load(Fixture(18, truncate: true));
        check(broken.GameplayWarning is not null && broken.Units.Count == 2, "truncated path disables movement while retaining unit inspection");
        throws(() => broken.MoveUnit(0, 7, 8), "uncertain gameplay parse prevents movement");
        check(broken.Serialize().SequenceEqual(File.ReadAllBytes(broken.SourcePath)), "truncated gameplay bytes preserved");
    }
}
