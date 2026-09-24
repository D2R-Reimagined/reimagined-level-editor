using D2RLevel.Core;
using Reimagined.Integration;

internal static class CompanionChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        var root = Path.Combine(folder, "linked-studio"); var data = Path.Combine(root, "data");
        var assets = Path.Combine(folder, "base-assets"); Directory.CreateDirectory(Path.Combine(assets, "hd"));
        Directory.CreateDirectory(Path.Combine(assets, "global/excel"));
        File.WriteAllText(Path.Combine(assets, "global/excel/levels.txt"), "Id\tName\n137\tVanilla\n");
        var resolver = new AssetResolver(assets);
        var snapshot = Path.Combine(root, ".studio/integration/standard/revision"); Directory.CreateDirectory(Path.Combine(snapshot, "global/excel"));
        var table = Path.Combine(snapshot, "global/excel/levels.txt"); File.WriteAllText(table, "Id\tName\n137\tAuthored\n");
        var pointer = Path.Combine(root, ".studio/integration/standard/current.json");
        IntegrationFiles.Write(pointer, new { projectId = "test", profile = "standard", root = snapshot, tables = new Dictionary<string, object> { ["global/excel/levels.txt"] = new { sourceIds = new[] { "stable-row" } } } });
        try
        {
            StudioTableContext.Project = new("test", root, "standard", assets, pointer);
            var read = new GameDataTables(resolver, data).Read("levels");
            check(read.Rows.Single()["Name"] == "Authored", "Linked workspace reads Studio snapshot instead of base tables");
            check(StudioTableContext.SourceId("levels", read.Rows[0]) == "stable-row", "Return link retains authored source record identity");
            check(IntegrationFiles.Canonical(resolver.Resolve("data/global/excel/levels.txt")) == IntegrationFiles.Canonical(table), "Legacy DS1 readers use the same Studio table snapshot");
            var scene = Path.Combine(root, "compatibility/standard/scene.json"); var map = Path.Combine(root, "compatibility/standard/scene.ds1");
            Directory.CreateDirectory(Path.GetDirectoryName(scene)!); File.WriteAllText(scene, "{}"); File.WriteAllBytes(map, [0]);
            IntegrationFiles.Write(pointer, new { projectId = "test", profile = "standard", root = snapshot, tables = new Dictionary<string, object> { ["global/excel/levels.txt"] = new { sourceIds = new[] { "stable-row" } } }, assetOverrides = new Dictionary<string, string> { ["hd/env/preset/room.json"] = scene, ["global/tiles/room.ds1"] = map } });
            StudioTableContext.Invalidate();
            var scenes = SceneWorkspace.Scan(data);
            check(scenes.Length == 1 && IntegrationFiles.Canonical(scenes[0].JsonPath) == IntegrationFiles.Canonical(scene) && IntegrationFiles.Canonical(scenes[0].Ds1Path) == IntegrationFiles.Canonical(map), "Linked workspace discovers profile-only scene pairs at their authoritative paths");
            File.Delete(table);
            check(new GameDataTables(resolver, data).Read("levels").Warning != null, "Missing authored snapshot fails visibly without vanilla fallback");
            StudioTableContext.Project = new("wrong", root, "standard", assets, pointer);
            throws(() => StudioTableContext.Resolve("levels"), "Snapshot cannot be used by the wrong project");
            throws(() => IntegrationFiles.Inside(root, "../escape"), "Companion scene paths cannot escape the project");
        }
        finally { StudioTableContext.Project = null; StudioTableContext.LogicalMap = null; }
        check(new GameDataTables(resolver, null).Read("levels").Rows.Single()["Name"] == "Vanilla", "Disconnecting restores standalone base-data behavior");
    }
}
