using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using D2RLevel.Assets;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private void GameplayPrefabs_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        try { var browser = CreatePrefabBrowser(); browser.Owner = this; browser.ShowDialog(); } catch (Exception ex) { Error(ex); }
    }
    private PrefabBrowser CreatePrefabBrowser()
    {
        var current = document ?? throw new InvalidOperationException(L.T("Open a workspace scene to use gameplay prefabs."));
        var map = pairedScene?.Collision?.Document ?? throw new InvalidOperationException(L.T("Open a workspace scene to use gameplay prefabs."));
        var links = placementLinks ?? throw new InvalidOperationException(L.T("Load the paired gameplay data first."));
        var assets = resolver ?? throw new InvalidOperationException(L.T("Choose an asset folder first."));
        string root = PresetPairing.Split(current.SourcePath, "hd/env/preset")!.Value.DataRoot;
        if (workspaceSession is null || Path.GetFullPath(root).Equals(Path.GetFullPath(assets.DataRoot), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.T("Open a mod workspace separate from the extracted assets to use gameplay prefabs."));
        if (links.Warning is { } warning) throw new InvalidOperationException(warning);
        Scene.CancelDrag(); var selection = SelectedEntities.ToArray();
        string? Resolve(string logical)
        {
            _ = assets.Resolve(logical);
            string local = logical.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ? new AssetResolver(root).ResolveForRead(logical) : SceneWorkspace.Inside(root, Path.Combine(root, logical[5..]));
            if (File.Exists(local)) return local;
            string fallback = assets.ResolveForRead(logical); return File.Exists(fallback) ? fallback : null;
        }
        (string Physical, string File, string[] Dependencies) Inspect(string logical)
        {
            string physical = Resolve(logical) ?? throw new FileNotFoundException("Missing prefab dependency: " + logical);
            string dataRoot = physical.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? root : assets.DataRoot;
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (logical.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string texture in ModelReader.Load(physical).TexturePaths ?? []) dependencies.Add(texture);
                if (!System.Text.RegularExpressions.Regex.IsMatch(logical, @"_lod\d+\.model$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    for (int i = 0; i <= 4; i++) { string lod = logical[..^6] + "_lod" + i + ".model"; if (Resolve(lod) is not null) dependencies.Add(lod); }
            }
            else if (logical.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(physical).Length > 16 * 1024 * 1024) throw new InvalidDataException("Prefab JSON dependency is too large.");
                void Visit(JsonNode? n)
                {
                    if (n is JsonValue v && v.TryGetValue<string>(out string? value) && value.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase)) dependencies.Add(value.Replace('\\', '/'));
                    else if (n is JsonObject o) foreach (var p in o) Visit(p.Value);
                    else if (n is JsonArray a) foreach (var child in a) Visit(child);
                }
                Visit(JsonNode.Parse(File.ReadAllText(physical)));
            }
            return (physical, "data/" + Path.GetRelativePath(dataRoot, physical).Replace('\\', '/'), dependencies.ToArray());
        }
        var catalog = GameplayCatalog.Load(assets, root, map.Act);
        return new(Path.Combine(root, ".rle-prefabs"), assets, current, map, links, selection, catalog, Inspect,
            async (folder, prefab, x, y, elevation, scale) =>
            {
                if (document != current || pairedScene?.Collision?.Document != map) throw new InvalidOperationException("The scene changed; reopen the prefab browser.");
                var fresh = GameplayCatalog.Load(assets, root, map.Act);
                links.ValidatePrefabPlacement(prefab, x, y, elevation, scale, fresh);
                var installed = prefab.InstallAssets(folder, root, Resolve);
                PrefabPlacement placement;
                try { placement = links.PlacePrefab(prefab, x, y, elevation, scale, fresh); }
                catch { foreach (string path in installed) File.Delete(SceneWorkspace.Inside(root, path)); throw; }
                await LoadScene(current, assets);
                foreach (var copy in placement.Models) if (Scene.GetItem(copy) is { } item) addedModels[copy] = item;
                SetSelection(placement.Models); Scene.FrameSelected(); RefreshState();
            }, () => { workspaceSession!.Save(current, map, links, connectionEdits); RefreshState(); });
    }
    private void SyncPrefab(PrefabPlacement placement)
    {
        if (document is null) return;
        foreach (var copy in placement.Models)
        {
            if (document.Entities.Contains(copy))
            {
                if (Scene.GetItem(copy) is null && addedModels.TryGetValue(copy, out var item)) Scene.AddItem(item);
                removedModels.Remove(copy);
            }
            else { if (Scene.GetItem(copy) is { } item) addedModels[copy] = item; Scene.RemoveItem(copy); removedModels.Add(copy); }
        }
        Search.Text = ""; Filter(); SetSelection(placement.Models.Where(document.Entities.Contains));
    }
}
