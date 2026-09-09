using System.Buffers.Binary;
using D2RLevel.Core;

internal static class CollisionChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string source = Path.Combine(folder, "collision.ds1");
        using (var w = new BinaryWriter(File.Create(source)))
        {
            w.Write(18); w.Write(2); w.Write(1); w.Write(0); w.Write(2);
            w.Write(1); w.Write(System.Text.Encoding.ASCII.GetBytes("retained.dt1\0"));
            w.Write(2); w.Write(2);
            // Wall 1: a corner and an existing whole-tile override.
            foreach (uint v in new uint[] { 0, 0, 0x00100901, 0x00120901, 0, 0 }) w.Write(v);
            foreach (int v in new[] { 0, 0, 3, 1, 0, 0 }) w.Write(v);
            w.Write(new byte[6 * 4 * 2]); // Wall 2 plus its orientation layer.
            foreach (uint v in new uint[] { 0x80100701, 0, 0x00100701, 0x00120701, 0, 0 }) w.Write(v);
            foreach (uint v in new uint[] { 0, 0x00100701, 0, 0, 0, 0 }) w.Write(v);
            w.Write(new byte[6 * 4 * 2]); // Shadow and tag layers.
            w.Write(0); w.Write(0); w.Write(0); // Empty object/group/path counts.
            w.Write(new byte[] { 1, 3, 7, 9, 42 }); // Future opaque bytes must survive.
        }
        var original = File.ReadAllBytes(source); var doc = Ds1CollisionDocument.Load(source);
        check(doc.Width == 3 && doc.Height == 2 && doc.Floors.Count == 2 && doc.Walls.Count == 2, "collision parses interleaved walls/orientations and two floors");
        check(doc.Serialize().SequenceEqual(original) && !doc.IsDirty, "DS1 pristine bytes preserved");
        check(doc.Paint([(0, 0), (1, 0), (0, 0)], true) == 2, "paint uses first present floor and deduplicates stroke");
        var edited = doc.Serialize();
        var differences = Enumerable.Range(0, edited.Length).Where(i => edited[i] != original[i]).ToArray();
        check(differences.SequenceEqual(new[] { doc.Floors[0].Offset + 2, doc.Floors[1].Offset + 6 }) && differences.All(i => (edited[i] ^ original[i]) == 2), "DS1 edit changes only bit 17 at intended floor byte offsets");
        check(doc.Paint([(0, 0)], true) == 0 && doc.Paint([(1, 1)], true) == 0, "already blocked and missing floors are no-ops");
        doc.Undo(); check(!doc.CanUndo && doc.Serialize().SequenceEqual(original), "one undo restores entire stroke including opaque trailer");
        doc.Redo(); check(doc.Serialize().SequenceEqual(edited), "stroke redo is exact");
        var output = Path.Combine(folder, "collision-copy.ds1"); doc.SaveCopy(output);
        check(!doc.IsDirty && Ds1CollisionDocument.Load(output).HasOverride(0, 0), "collision save/reopen retains override");
        doc.Undo(); check(doc.IsDirty, "undo after collision save is dirty");
        doc.Paint([(0, 1)], false);
        check(!doc.HasOverride(0, 1) && !doc.CanRedo, "clear removes floor and wall overrides and branches history");
        doc.Undo(); check(doc.HasOverride(0, 1), "undo clear restores both layers");
        var beforeFailure = doc.Serialize();
        throws(() => doc.Paint([(0, 0), (99, 0)], true), "reject out-of-bounds stroke");
        check(doc.Serialize().SequenceEqual(beforeFailure), "invalid stroke leaves no partial edits");
        throws(() => doc.SaveCopy(source), "collision export protects source");
        doc.SaveCopy(output); check(File.ReadAllBytes(output + ".bak").SequenceEqual(edited), "collision copy replacement retains backup");
        check(File.ReadAllBytes(source).SequenceEqual(original), "collision source remains unchanged");
        var flags = new byte[25]; flags[0] = 1; flags[24] = 8;
        var wallA = new byte[25]; wallA[2] = 1;
        var wallB = new byte[25]; wallB[3] = 1;
        var collision = new LegacyCollision(doc, [new("floor", 0, 1, 7, flags), new("wall", 3, 1, 9, wallA), new("corner", 4, 1, 9, wallB)]);
        var result = collision.At(2, 0);
        check(result.Flags[0] == 1 && result.Flags[2] == 1 && result.Flags[3] == 1 && result.Flags[24] == 8 && !result.Unresolved, "collision unions floor wall and corner subtiles");
        check(collision.At(1, 1).NoFloor && collision.At(1, 1).BlockedSubtiles == 25, "missing floor is blocked");
        check(collision.At(0, 1).Override && collision.At(0, 1).BlockedSubtiles == 25, "DS1 override blocks all subtiles");
        var uncertain = new LegacyCollision(doc, [new("floor1", 0, 1, 7, flags), new("floor2", 0, 1, 7, new byte[25])]);
        check(uncertain.At(2, 0).VariantDependent && uncertain.At(2, 0).Unresolved, "variant-dependent and unresolved collision are explicit");
        doc.Paint([(0, 0)], true); doc.Paint([(0, 0)], false);
        check(collision.At(0, 0).BlockedSubtiles == 2, "clearing override preserves DT1 blocking");
        var dt1 = new byte[276 + 96];
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(dt1.AsSpan(offset, 4), value);
        Write(0, 7); Write(4, 6); Write(268, 1); Write(272, 276); Write(300, 1); Write(304, 7);
        dt1[276 + 40] = 1; dt1[276 + 40 + 24] = 8;
        var dt1Path = Path.Combine(folder, "collision.dt1"); File.WriteAllBytes(dt1Path, dt1);
        var tile = LegacyCollision.ReadTiles(dt1Path).Single();
        check(tile.Flags[20] == 1 && tile.Flags[4] == 8 && tile.Main == 1 && tile.Sub == 7, "DT1 flag rows map bottom-to-top without transposing X");
        File.WriteAllBytes(dt1Path, dt1[..^1]); throws(() => LegacyCollision.ReadTiles(dt1Path), "reject truncated DT1 collision headers");
        File.WriteAllBytes(source, original[..(doc.Floors[1].Offset + 24)]);
        throws(() => Ds1CollisionDocument.Load(source), "reject missing DS1 shadow/tag data");
        for (int version = 16; version <= 17; version++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(0, 4), version); File.WriteAllBytes(source, original);
            check(Ds1CollisionDocument.Load(source).Serialize().SequenceEqual(original), $"DS1 v{version} collision round-trip");
        }
    }
}
