using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed record PrefabModel(JsonObject Entity, int? Unit, LinkedTile[] Collision);
public sealed record PrefabUnit(string Key, GameplayAssetKind Kind, int Type, uint Flags, int X, int Y, Ds1PathPoint[] Patrol, string DefinitionHash);
public sealed record PrefabAsset(string Logical, string File, string Sha256, long Length);
public sealed record PrefabPlacement(PresetEntity[] Models, int[] Units);
public sealed record GameplayPrefab(int Version, string Name, int Act, double UnitsPerTile, PrefabModel[] Models, PrefabUnit[] Units, string[] References)
{
    public const string Manifest = "prefab.json";
    public PrefabAsset[] Assets { get; init; } = [];
    public JsonObject Dependencies { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    internal JsonObject? SourceDependencies { get; init; }
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string DefinitionHash(GameplayAsset asset) => Hash(Encoding.UTF8.GetBytes(string.Join("\n", asset.References
        .Where(r => !new[] { "monpreset.txt", "objpreset.txt" }.Contains(Path.GetFileName(r.SourcePath).ToLowerInvariant()))
        .Select(r => string.Join("\t", r.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))).Order(StringComparer.Ordinal))));
    internal static string[] AssetReferences(JsonNode node)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(JsonNode? n)
        {
            if (n is JsonValue v && v.TryGetValue<string>(out string? value) && value.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase)) paths.Add(value.Replace('\\', '/'));
            else if (n is JsonObject o) foreach (var pair in o) Visit(pair.Value);
            else if (n is JsonArray a) foreach (var child in a) Visit(child);
        }
        Visit(node); return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static void ValidateModel(PresetEntity model)
    {
        string[] supported = ["TransformDefinitionComponent", "ModelDefinitionComponent", "ModelVariationDefinitionComponent", "ModelPlatformTierComponent", "PhysicsBodyDefinitionComponent"];
        if (!GroupMovement.CanGroup(model) || model.Components.Any(c => !supported.Contains(c["type"]?.GetValue<string>())) || model.Components.Count(c => c["type"]?.GetValue<string>() == "TransformDefinitionComponent") != 1)
            throw new InvalidDataException("Prefabs support standalone models with transform, model, variation, platform-tier and physics components. Terrain, hierarchy and other component types must be handled separately.");
        model.Transform.Validate();
    }
    public void Validate()
    {
        GridCalibration.Validate(UnitsPerTile);
        if (Version != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Act is < 1 or > 5 || Models is null || Units is null || References is null || Assets is null || Dependencies is null ||
            Models.Length > 256 || Units.Length > 512 || Models.Length + Units.Length == 0 || References.Length > 10000 || Assets.Length > 10000)
            throw new InvalidDataException("Invalid or unsupported gameplay prefab.");
        var linked = new HashSet<int>();
        foreach (var model in Models)
        {
            if (model is null || model.Entity is null || model.Collision is null || model.Collision.Length > 4096 || model.Collision.Any(t => t is null)) throw new InvalidDataException("Invalid prefab model or footprint.");
            ValidateModel(new(model.Entity, 0));
            if (model.Unit is { } unit && (unit < 0 || unit >= Units.Length || !linked.Add(unit))) throw new InvalidDataException("Invalid or shared prefab unit ownership.");
        }
        foreach (var unit in Units)
            if (unit is null || string.IsNullOrWhiteSpace(unit.Key) || unit.Type is not (1 or 2) || !Enum.IsDefined(unit.Kind) || unit.Kind == GameplayAssetKind.Spawn || unit.Patrol is null || unit.Patrol.Length > 10000 || unit.Patrol.Any(p => p is null) || unit.DefinitionHash?.Length != 64)
                throw new InvalidDataException("Invalid prefab gameplay recipe.");
        if (Units.Select(u => (u.X, u.Y)).Distinct().Count() != Units.Length) throw new InvalidDataException("Prefab units cannot share a gameplay anchor.");
        foreach (string path in References.Concat(Assets.SelectMany(a => a is null ? throw new InvalidDataException("Invalid prefab asset.") : new[] { a.Logical, a.File }))) ValidatePath(path);
        foreach (var category in Dependencies)
        {
            if (category.Value is not JsonArray entries || entries.Any(n => n is not JsonObject || n["path"] is not JsonValue)) throw new InvalidDataException("Invalid prefab dependency declaration.");
            foreach (var entry in entries) ValidatePath(entry!["path"]!.GetValue<string>());
        }
        if (Assets.Any(a => a.Sha256?.Length != 64 || a.Length < 0 || a.Length > 256 * 1024 * 1024) || Assets.Sum(a => a.Length) > 1024L * 1024 * 1024 || Assets.Select(a => a.Logical).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Assets.Length)
            throw new InvalidDataException("Invalid prefab dependency manifest.");
    }
    internal static void ValidatePath(string path) => LevelProject.ValidateAssetPath(path, "data/hd/", "");
    public PresetDocument PreviewDocument() => PresetDocument.FromPrefab(Models.Select(m => m.Entity));
    public static GameplayPrefab Load(string folder)
    {
        string path = SceneWorkspace.Inside(folder, Path.Combine(folder, Manifest));
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Prefab manifest exceeds 16 MiB.");
        var value = JsonSerializer.Deserialize<GameplayPrefab>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty prefab."); value.Validate();
        return value;
    }
    /// <summary>Publish a new directory containing the manifest and its asset closure; never replace an existing prefab.</summary>
    public GameplayPrefab Save(string folder, Func<string, (string Physical, string File, string[] Dependencies)> inspect)
    {
        Validate(); folder = Path.GetFullPath(folder);
        string parent = Path.GetDirectoryName(folder) ?? throw new InvalidDataException("Choose a prefab directory.");
        if (!Directory.Exists(parent) || Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Choose a new prefab folder.");
        SceneWorkspace.Inside(parent, Path.Combine(folder, Manifest));
        string stage = Path.Combine(parent, ".prefab-stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var pending = new Queue<string>(References.Concat(Models.SelectMany(m => AssetReferences(m.Entity))));
            var found = new Dictionary<string, PrefabAsset>(StringComparer.OrdinalIgnoreCase); long total = 0;
            while (pending.TryDequeue(out string? logical))
            {
                ValidatePath(logical); if (found.ContainsKey(logical)) continue;
                if (found.Count >= 10000) throw new InvalidDataException("Prefab exceeds 10,000 dependencies.");
                var asset = inspect(logical); ValidatePath(asset.File);
                long size = new FileInfo(asset.Physical).Length; total = checked(total + size);
                if (size > 256 * 1024 * 1024 || total > 1024L * 1024 * 1024) throw new InvalidDataException("Prefab dependency bundle exceeds its size limit.");
                byte[] bytes = File.ReadAllBytes(asset.Physical); string hash = Hash(bytes);
                string target = SceneWorkspace.Inside(stage, Path.Combine(stage, "assets", asset.File)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && Hash(File.ReadAllBytes(target)) != hash) throw new InvalidDataException("Conflicting prefab dependency: " + asset.File);
                File.WriteAllBytes(target, bytes); found.Add(logical, new(logical, asset.File, hash, bytes.Length));
                foreach (string dependency in asset.Dependencies) pending.Enqueue(dependency);
                foreach (string dependency in AssetReferences(FilterDependencies(SourceDependencies ?? Dependencies, [logical]))) pending.Enqueue(dependency);
            }
            var complete = this with { Assets = found.Values.OrderBy(a => a.Logical).ToArray(), Dependencies = FilterDependencies(SourceDependencies ?? Dependencies, found.Keys), SourceDependencies = null }; complete.Validate();
            File.WriteAllBytes(Path.Combine(stage, Manifest), JsonSerializer.SerializeToUtf8Bytes(complete, Options)); _ = Load(stage);
            Directory.Move(stage, folder); return complete;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(SceneWorkspace.Inside(parent, stage), true); }
    }
    public void VerifyAssets(string folder)
    {
        Validate();
        var supplied = Assets.Select(a => a.Logical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (References.Concat(Models.SelectMany(m => AssetReferences(m.Entity))).Concat(AssetReferences(Dependencies)).Any(p => !supplied.Contains(p))) throw new InvalidDataException("Prefab dependency manifest is incomplete.");
        foreach (var asset in Assets)
        {
            string path = SceneWorkspace.Inside(folder, Path.Combine(folder, "assets", asset.File));
            if (!File.Exists(path) || new FileInfo(path).Length != asset.Length || Hash(File.ReadAllBytes(path)) != asset.Sha256) throw new InvalidDataException("Prefab asset is missing or changed: " + asset.Logical);
        }
    }
    /// <summary>Validate all conflicts before installing only absent dependencies. Existing files are never overwritten.</summary>
    public string[] InstallAssets(string folder, string workspace, Func<string, string?> resolve)
    {
        VerifyAssets(folder); var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in Assets)
        {
            string? current = resolve(asset.Logical);
            if (current is not null && File.Exists(current))
            {
                if (Hash(File.ReadAllBytes(current)) != asset.Sha256) throw new InvalidDataException("Target asset differs from prefab: " + asset.Logical);
                continue;
            }
            string target = SceneWorkspace.Inside(workspace, Path.Combine(workspace, asset.File[5..]));
            if (File.Exists(target))
            {
                if (Hash(File.ReadAllBytes(target)) != asset.Sha256) throw new InvalidDataException("Target dependency conflicts: " + asset.File);
            }
            else copies[target] = SceneWorkspace.Inside(folder, Path.Combine(folder, "assets", asset.File));
        }
        var installed = new List<string>();
        try { foreach (var (target, source) in copies) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false); installed.Add(target); } return installed.ToArray(); }
        catch { foreach (var path in installed) File.Delete(path); throw; }
    }
    internal static JsonObject FilterDependencies(JsonObject source, IEnumerable<string> paths)
    {
        var include = paths.ToHashSet(StringComparer.OrdinalIgnoreCase); var result = new JsonObject();
        foreach (var category in source)
        {
            if (category.Value is not JsonArray values) throw new InvalidDataException("Expected a dependency declaration array: " + category.Key);
            var selected = values.OfType<JsonObject>().Where(v => v["path"] is JsonValue p && p.TryGetValue<string>(out var path) && include.Contains(path.Replace('\\', '/'))).Select(v => v.DeepClone()).ToArray();
            if (selected.Length > 0) result[category.Key] = new JsonArray(selected);
        }
        return result;
    }
}

public sealed partial class PresetDocument
{
    internal JsonObject PrefabDependencySnapshot() => root["dependencies"] is null ? new() : root["dependencies"]?.DeepClone() as JsonObject ?? throw new InvalidDataException("Expected preset dependency object.");
    internal static PresetDocument FromPrefab(IEnumerable<JsonObject> source)
    {
        var value = new JsonObject { ["entities"] = new JsonArray(source.Select(e => e.DeepClone()).ToArray()) };
        return new(value, Encoding.UTF8.GetBytes(value.ToJsonString()), "prefab-preview.json");
    }
    internal PresetEntity[] InsertPrefab(GameplayPrefab prefab, Vector3d offset)
    {
        var before = root["dependencies"]?.DeepClone(); bool had = root.ContainsKey("dependencies");
        if (before is not null && before is not JsonObject) throw new InvalidDataException("Expected preset dependency object.");
        var dependencies = before?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var category in prefab.Dependencies)
        {
            if (dependencies[category.Key] is not null && dependencies[category.Key] is not JsonArray) throw new InvalidDataException("Invalid target dependency category: " + category.Key);
            var values = dependencies[category.Key] as JsonArray ?? new JsonArray();
            foreach (var entry in ((JsonArray)category.Value!).OfType<JsonObject>())
            {
                string path = entry["path"]!.GetValue<string>();
                var existing = values.OfType<JsonObject>().Where(v => string.Equals(v["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (existing.Length > 1 || existing.Length == 1 && !JsonNode.DeepEquals(existing[0], entry)) throw new InvalidDataException("Dependency declaration conflicts with this scene: " + path);
                if (existing.Length == 0) values.Add(entry.DeepClone());
            }
            if (dependencies[category.Key] is null) dependencies[category.Key] = values;
        }
        foreach (var group in prefab.Assets.Where(a => a.Logical.EndsWith(".model", StringComparison.OrdinalIgnoreCase) || a.Logical.EndsWith(".texture", StringComparison.OrdinalIgnoreCase)).GroupBy(a => a.Logical.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ? "models" : "textures"))
        {
            if (dependencies[group.Key] is not null && dependencies[group.Key] is not JsonArray) throw new InvalidDataException("Invalid dependency category.");
            var array = dependencies[group.Key] as JsonArray ?? new JsonArray();
            foreach (var a in group) if (!array.OfType<JsonObject>().Any(v => string.Equals(v["path"]?.GetValue<string>(), a.Logical, StringComparison.OrdinalIgnoreCase))) array.Add(new JsonObject { ["path"] = a.Logical });
            if (dependencies[group.Key] is null) dependencies[group.Key] = array;
        }
        var names = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); var ids = entities.Select(e => e.Id).ToHashSet(); var copies = new List<PresetEntity>();
        foreach (var model in prefab.Models)
        {
            var data = (JsonObject)model.Entity.DeepClone(); var copy = new PresetEntity(data, nextIndex++); var t = copy.Transform;
            var position = new Vector3d(t.Position.X + offset.X, t.Position.Y + offset.Y, t.Position.Z + offset.Z); (t with { Position = position }).Validate();
            uint id; do { id = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1); } while (!ids.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            string basename = copy.Name + "_prefab", name = basename; for (int i = 2; !names.Add(name); i++) name = basename + i;
            data["id"] = id; data["name"] = name; PatchVector(copy.TransformNode!, "position", position, t.Position); copies.Add(copy);
        }
        var entitiesNode = (JsonArray)root["entities"]!;
        void Insert() { foreach (var copy in copies) { entitiesNode.Add(copy.Data); entities.Add(copy); } root["dependencies"] = dependencies.DeepClone(); dirtyCache = null; }
        void Remove() { foreach (var copy in copies) { entitiesNode.Remove(copy.Data); entities.Remove(copy); } if (had) root["dependencies"] = before?.DeepClone(); else root.Remove("dependencies"); dirtyCache = null; }
        Insert(); History.Record(Remove, Insert); return copies.ToArray();
    }
}
