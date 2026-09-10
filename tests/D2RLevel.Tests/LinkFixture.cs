using D2RLevel.Core;

/// <summary>
/// A synthetic 4x4 v18 DS1 paired with a two-model preset. Floors are non-empty so
/// every tile is blockable, and two type-1 records carry patrol paths.
/// </summary>
internal static class LinkFixture
{
    public static (PresetDocument Json, Ds1CollisionDocument Ds1, PlacementLinks Links) Create(string folder, bool sharedAnchor = false)
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
}
