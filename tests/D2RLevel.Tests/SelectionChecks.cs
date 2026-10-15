using D2RLevel.Core;

internal static class SelectionChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        ModelFootprint Box(string name, double x, double z, double size = 2) => new(name, x, z, x + size, z + size);
        var selected = Box("chest", 0, 0);
        check(SelectionNeighbors.Gap(selected, Box("crate", 1, 1)) == 0 && SelectionNeighbors.Gap(selected, Box("rock", 2, 0)) == 0, "overlapping and touching footprints have no gap");
        check(Math.Abs(SelectionNeighbors.Gap(selected, Box("tree", 5, 6)) - 5) < 1e-9, "gap is the shortest ground distance between corners");

        var scene = new[] { Box("touching", 2, 0), Box("near", 4, 0), Box("diagonal", 5, 6), Box("far", 40, 40) };
        var hits = SelectionNeighbors.Find([selected], scene.Select(f => (f.Name, f)), 5, 10);
        check(hits.Select(h => h.Item).SequenceEqual(["touching", "near", "diagonal"]), "nearby models are listed nearest first and far ones are left out");
        check(SelectionNeighbors.Find([selected], scene.Select(f => (f.Name, f)), 5, 1).Single().Item == "touching", "nearby list respects its limit");
        // A spread selection measures from each member, not from the box around all of them.
        var spread = new[] { selected, Box("well", 40, 0) };
        var between = SelectionNeighbors.Find(spread, new[] { Box("middle", 20, 0) }.Select(f => (f.Name, f)), 10, 10);
        check(between.Length == 0, "models between spread-out selected models are not counted as touching");
        check(SelectionNeighbors.Find(Array.Empty<ModelFootprint>(), scene.Select(f => (f.Name, f)), 5, 10).Length == 0 &&
            SelectionNeighbors.Find([selected], scene.Select(f => (f.Name, f)), double.NaN, 10).Length == 0, "no selection or an invalid range finds nothing");
    }
}
