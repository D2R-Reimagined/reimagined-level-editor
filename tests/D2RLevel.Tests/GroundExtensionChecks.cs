using System.Buffers.Binary;
using D2RLevel.Assets;
using D2RLevel.Core;

internal static class GroundExtensionChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        PresetPairing.RequireRuntimeDs1Path("act1/rlecave/cave.ds1");
        PresetPairing.RequireRuntimeDs1Path(new string('a', 37) + ".ds1");
        throws(() => PresetPairing.RequireRuntimeDs1Path(new string('a', 38) + ".ds1"), "preset open path rejects 60 bytes including prefix");
        throws(() => PresetPairing.RequireRuntimeDs1Path("act1/rle_cave_growth_oct1/cave_growth_oct1.ds1"), "preset open path rejects the reproduced truncated cave filename");
        throws(() => PresetPairing.RequireRuntimeDs1Path("act1/café.ds1"), "preset open path rejects ambiguous non-ASCII encoding");
        var rejectedOutput = Path.Combine(folder, "overlong-export");
        throws(() => GroundExtensionExporter.Export(Path.Combine(folder, "hd/env/preset/act1/caves/cave.json"),
            Path.Combine(folder, "global/tiles/act1/rle_cave_growth_oct1/cave_growth_oct1.ds1"), null!, null!,
            new(0, 0, 1, 1, 0, 0, 0), rejectedOutput), "export refuses truncated runtime paths before reading assets");
        check(!Directory.Exists(rejectedOutput), "rejected runtime path publishes no output");
        string path = Path.Combine(folder, "ground.ds1");
        var map = Ds1CollisionDocument.Create(path, 12, 12, 1, Ds1CollisionDocument.FloorKey(0, 0), wallLayers: 2);
        var bytes = map.Serialize();
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
        int Wall(int x, int y) => map.Walls[0].Offset + (y * map.Width + x) * 4;
        foreach (var (x, slot) in new[] { (3, 0), (5, 30) })
        {
            Put(Wall(x, 3), 0x80000081u | (uint)slot << 20);
            Put(Wall(x, 3) + map.Width * map.Height * 4, 10);
        }
        Put(Wall(8, 8), 0x81); File.WriteAllBytes(path, bytes); map = Ds1CollisionDocument.Load(path);
        foreach (var x in new[] { 3, 5 })
        {
            throws(() => map.ExtendGround(x, 3, x + 1, 4, 1, 1), "ground extension rejects entrance and special markers");
            check(map.Serialize().SequenceEqual(bytes), "rejected marker overlap preserves every DS1 byte");
        }
        throws(() => map.ExtendGround(10, 10, 12, 12, 1, 1), "ground extension preserves final border row and column");
        map.ExtendGround(8, 8, 9, 9, 1, 1);
        check(map.Cell(map.Walls[0], 8, 8) == 0 && map.ExitTiles().Count == 2, "ground extension clears selected scenery walls and preserves exits");
        var edited = map.Serialize(); map.Undo(); check(map.Serialize().SequenceEqual(bytes), "ground extension undo restores bytes");
        map.Redo(); check(map.Serialize().SequenceEqual(edited), "ground extension redo reproduces bytes");

        const string donor = "data/hd/test.model", target = "data/hd/clipped.model";
        byte[] catalog = new byte[24 + 44 + 12 + 64];
        void I(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(catalog.AsSpan(at, 4), value);
        I(0, 1); I(4, 20); I(8, 1); I(12, 56); I(16, 2); I(20, 60);
        I(24, unchecked((int)GroundModelCatalog.Key(donor))); I(56, 1); I(60, 8); I(68, 100); I(72, 2); I(76, 4);
        for (int i = 80; i < catalog.Length; i++) catalog[i] = (byte)i;
        var registered = GroundModelCatalog.Register(catalog, donor, target, [1]);
        int Read(int at) => BinaryPrimitives.ReadInt32LittleEndian(registered.AsSpan(at, 4));
        check(Read(0) == 2 && Read(8) == 6 && Read(16) == 7, "catalog retains original records and appends five clipped LODs");
        int meshStart = 20 + Read(20);
        check(registered.AsSpan(meshStart, 64).SequenceEqual(catalog.AsSpan(80, 64)), "catalog preserves original opaque mesh descriptors");
        check(Enumerable.Range(0, 5).All(i => registered.AsSpan(meshStart + 64 + i * 32, 32).SequenceEqual(catalog.AsSpan(112, 32))), "each clipped LOD uses only its retained donor mesh");
        throws(() => GroundModelCatalog.Register(catalog, donor, target, [2]), "catalog rejects mismatched mesh selection");
        throws(() => GroundModelCatalog.Register(registered, donor, target, [0]), "catalog rejects duplicate model identities");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => GroundExtensionExporter.Export("missing", "missing", null!, null!, new(0, 0, 1, 1, 0, 0, 0), Path.Combine(folder, "canceled"), cancel.Token), "pre-canceled export stops before source access");
        check(!Directory.Exists(Path.Combine(folder, "canceled")), "cancellation does not publish a destination");
        byte[] physics = new byte[160];
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(physics.AsSpan(at), value);
        void N(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(physics.AsSpan(at), value);
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(physics.AsSpan(at), value);
        Q(0, 48); Q(8, 80); Q(16, 128); N(24, 1); N(28, 3); N(32, 1);
        F(60, 2); F(64, 2); F(68, 2);
        F(96, 2); F(116, 2); F(120, 2); N(132, 1); N(136, 2);
        var mesh = PhysicsMeshBounds.Read(physics);
        mesh.RequireFloorBelow(new(0, 0, 2, 2, 2));
        check(mesh.WorldVertices(System.Numerics.Matrix4x4.CreateTranslation(10, 0, 0)).Min(p => p.X) == 10, "physics bounds use the entity transform");
        throws(() => mesh.RequireFloorBelow(new(0, 0, 2, 2, 0)), "retained terrain physics cannot protrude through the extension");
        mesh.RequireFloorBelow(new(10, 10, 12, 12, 0));
        N(128, 3); throws(() => PhysicsMeshBounds.Read(physics), "physics rejects out-of-range triangle indices"); N(128, 0);
        F(80, float.NaN); throws(() => PhysicsMeshBounds.Read(physics), "physics rejects nonfinite positions"); F(80, 0);
        Q(16, 124); throws(() => PhysicsMeshBounds.Read(physics), "physics rejects unknown packed layouts");
    }
}
