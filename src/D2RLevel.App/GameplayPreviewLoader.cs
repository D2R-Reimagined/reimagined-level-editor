using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record GameplayPreview(NpcVisual Visual, string Note);

internal static class GameplayPreviewLoader
{
    public static GameplayPreview Load(GameplayAsset entry, AssetResolver? assets, string? workspaceRoot, CancellationToken token)
    {
        if (assets is null) return Missing(entry, L.T("Choose an asset folder to preview HD models."));
        try
        {
            string Resolve(string logical, bool model = false)
            {
                string fallback = model ? assets.ResolvePreviewModel(logical, 1) : assets.Resolve(logical);
                if (workspaceRoot is not null)
                {
                    // A mod can contain only tables. Do not require a separate hd folder.
                    if (model && Directory.Exists(Path.Combine(workspaceRoot, "hd")))
                    {
                        string candidate = new AssetResolver(workspaceRoot).ResolvePreviewModel(logical, 1);
                        if (File.Exists(candidate)) return candidate;
                    }
                    string local = Path.Combine(workspaceRoot, logical[5..]);
                    if (File.Exists(local)) return local;
                }
                return fallback;
            }
            string definition;
            string note = L.T("Static reference pose. Game animations, services and object states are not simulated.");
            if (entry.Kind == GameplayAssetKind.Object)
            {
                var mappings = JsonNode.Parse(File.ReadAllText(Resolve("data/hd/objects/objects.json")))!.AsObject();
                static string Key(string key) => string.Concat(key.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
                var matches = mappings.Where(p => Key(p.Key) == Key(entry.Key)).ToArray();
                if (matches.Length != 1 || matches[0].Value?["asset_path"]?.GetValue<string>() is not { } folder)
                    return Missing(entry, L.T("No unambiguous HD object name match. Placement remains available when its gameplay mapping is valid."));
                definition = $"data/hd/objects/{folder}/{matches[0].Key}.json";
                note = L.T("HD object name match · reference preview. Game states and dynamic collision are not simulated.");
            }
            else if (entry.MonsterClass is { } monster)
            {
                var mappings = JsonNode.Parse(File.ReadAllText(Resolve("data/hd/character/monsters.json")))!.AsObject();
                string appearance = mappings[monster]?.GetValue<string>() ?? monster;
                if (appearance.Length == 0 || appearance.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                    throw new InvalidDataException("Invalid HD character appearance.");
                definition = new[] { "npc", "enemy" }.Select(folder => $"data/hd/character/{folder}/{appearance}.json")
                    .FirstOrDefault(path => File.Exists(Resolve(path))) ?? throw new FileNotFoundException("HD character definition unavailable.");
            }
            else return Missing(entry, L.T("This entry has no fixed character or object appearance."));
            var root = JsonNode.Parse(File.ReadAllText(Resolve(definition)))!.AsObject();
            var components = root["entities"]!.AsArray().OfType<JsonObject>()
                .SelectMany(e => e["components"]!.AsArray().OfType<JsonObject>()).ToArray();
            var transform = components.FirstOrDefault(c => (string?)c["type"] == "TransformDefinitionComponent");
            double Scale(string axis) => (double?)transform?["scale"]?[axis] ?? 1;
            var geometry = new Model3DGroup { Transform = new ScaleTransform3D(Scale("x"), Scale("y"), Scale("z")) };
            int missing = 0;
            foreach (var model in components.Where(c => (string?)c["type"] == "ModelDefinitionComponent"))
            {
                token.ThrowIfCancellationRequested();
                var loaded = SceneLoader.Load(PresetDocument.ModelPreview((string)model["filename"]!), assets, new Progress<string>(), token, false, Resolve);
                if (loaded.Items.Count == 0 || loaded.Items[0].IsPlaceholder) { missing++; continue; }
                geometry.Children.Add(loaded.Items[0].Geometry);
            }
            if (geometry.Children.Count == 0) return Missing(entry, L.T("HD meshes unavailable. A valid gameplay entry can still be placed."));
            geometry.Freeze();
            return new(new(entry.Key, geometry, false), definition + "\n" + note + (missing > 0 ? "\n" + L.T("{0} mesh part(s) unavailable; preview is incomplete.", missing) : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Missing(entry, L.T("Preview unavailable: {0}", ex.Message)); }
    }
    private static GameplayPreview Missing(GameplayAsset entry, string note) => new(new(entry.Key, SceneViewport.Placeholder(), true), note);
}
