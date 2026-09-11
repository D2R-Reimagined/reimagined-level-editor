using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public readonly record struct Vector3d(double X, double Y, double Z);
public readonly record struct Quaterniond(double X, double Y, double Z, double W);
public readonly record struct EntityTransform(Vector3d Position, Quaterniond Orientation, Vector3d Scale)
{
    public void Validate()
    {
        double[] values = [Position.X, Position.Y, Position.Z, Orientation.X, Orientation.Y,
            Orientation.Z, Orientation.W, Scale.X, Scale.Y, Scale.Z];
        if (values.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("Transform values must be finite numbers.");
        var length = Math.Sqrt(Orientation.X * Orientation.X + Orientation.Y * Orientation.Y +
            Orientation.Z * Orientation.Z + Orientation.W * Orientation.W);
        if (Math.Abs(length - 1) > 0.001)
            throw new ArgumentException("Rotation quaternion must have length 1 (within 0.001).");
        if (Scale.X == 0 || Scale.Y == 0 || Scale.Z == 0)
            throw new ArgumentException("Scale cannot be zero.");
    }
}

public sealed class PresetEntity
{
    internal JsonObject Data { get; }
    public int Index { get; }
    public int? GameplayUnitIndex { get; private init; }
    public static PresetEntity GameplayPreview(int index, string name, EntityTransform transform)
    {
        var data = new JsonObject { ["id"] = "ds1:" + index, ["name"] = name,
            ["components"] = new JsonArray(new JsonObject { ["type"] = "TransformDefinitionComponent" }) };
        var entity = new PresetEntity(data, -index - 1) { GameplayUnitIndex = index };
        entity.UpdateGameplayTransform(transform); return entity;
    }
    public void UpdateGameplayTransform(EntityTransform transform)
    {
        if (GameplayUnitIndex is null) throw new InvalidOperationException("Only gameplay preview transforms can be updated directly.");
        transform.Validate(); var t = TransformNode!;
        t["position"] = new JsonObject { ["x"] = transform.Position.X, ["y"] = transform.Position.Y, ["z"] = transform.Position.Z };
        t["orientation"] = new JsonObject { ["x"] = transform.Orientation.X, ["y"] = transform.Orientation.Y, ["z"] = transform.Orientation.Z, ["w"] = transform.Orientation.W };
        t["scale"] = new JsonObject { ["x"] = transform.Scale.X, ["y"] = transform.Scale.Y, ["z"] = transform.Scale.Z };
    }
    public string Name => Data["name"]?.GetValue<string>() ?? $"Entity {Index}";
    public string Id => Data["id"]?.ToJsonString() ?? "(none)";
    public bool IsTerrain => Components.Any(c => c["type"]?.GetValue<string>() == "TerrainDefinitionComponent");
    public IReadOnlyList<JsonObject> Components => (Data["components"] as JsonArray)?
        .OfType<JsonObject>().ToArray() ?? [];
    internal JsonObject? TransformNode => Components.FirstOrDefault(c => c["type"]?.GetValue<string>() == "TransformDefinitionComponent");
    public bool CanTransform => TransformNode is not null;
    public bool HasParent => Data.ContainsKey("parent") || Data.ContainsKey("parentId") ||
        Components.Any(c => (c["type"]?.GetValue<string>() ?? "").Contains("Parent", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> ModelPaths => Components.SelectMany(c => c["type"]?.GetValue<string>() switch
    {
        "ModelDefinitionComponent" => new[] { c["filename"]?.GetValue<string>() },
        "ModelVariationDefinitionComponent" => (c["variations"] as JsonArray)?.OfType<JsonObject>()
            .Select(v => v["filename"]?.GetValue<string>()) ?? [],
        _ => []
    }).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string? PreviewModel => ModelPaths.FirstOrDefault();
    public EntityTransform Transform
    {
        get
        {
            var t = TransformNode ?? throw new InvalidOperationException("Entity has no transform component.");
            return new(ReadVector(t["position"], 0),
                new(Number(t["orientation"], "x", 0), Number(t["orientation"], "y", 0),
                    Number(t["orientation"], "z", 0), Number(t["orientation"], "w", 1)), ReadVector(t["scale"], 1));
        }
    }
    public string RawJson => Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    internal PresetEntity(JsonObject data, int index) { Data = data; Index = index; }
    private static double Number(JsonNode? n, string field, double fallback) => n?[field]?.GetValue<double>() ?? fallback;
    private static Vector3d ReadVector(JsonNode? n, double fallback) =>
        new(Number(n, "x", fallback), Number(n, "y", fallback), Number(n, "z", fallback));
    public override string ToString() => Name;
}

public sealed class PresetDocument
{
    private readonly JsonObject root;
    private readonly byte[] originalBytes;
    public EditHistory History { get; } = new();
    private string savedJson;
    public string SourcePath { get; }
    public IReadOnlyList<PresetEntity> Entities { get; }
    private readonly List<PresetEntity> entities;
    private int nextIndex;
    private bool? dirtyCache;
    public bool IsDirty => dirtyCache ??= root.ToJsonString() != savedJson;
    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;

    private PresetDocument(JsonObject root, byte[] bytes, string path)
    {
        this.root = root;
        originalBytes = bytes;
        SourcePath = Path.GetFullPath(path);
        savedJson = root.ToJsonString();
        if (root["entities"] is not JsonArray array || array.Any(n => n is not JsonObject))
            throw new InvalidDataException("Expected a preset with an entities array of objects.");
        entities = array.OfType<JsonObject>().Select((e, i) => new PresetEntity(e, i)).ToList();
        Entities = entities.AsReadOnly(); nextIndex = entities.Count;
    }

    public static PresetDocument Load(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Preset exceeds the editor's 64 MiB limit.");
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Expected a JSON object.");
        return new(root, bytes, path);
    }

    public static PresetDocument ModelPreview(string modelPath)
    {
        var component = new JsonObject { ["type"] = "ModelDefinitionComponent", ["filename"] = modelPath };
        var entity = new JsonObject { ["name"] = Path.GetFileName(modelPath), ["components"] = new JsonArray(
            new JsonObject { ["type"] = "TransformDefinitionComponent" }, component) };
        var root = new JsonObject { ["entities"] = new JsonArray(entity) };
        return new(root, Encoding.UTF8.GetBytes(root.ToJsonString()), "model-preview.json");
    }

    public PresetDocument AuthoringScaffold(string path, bool keepScenery = false)
    {
        var copy = (JsonObject)root.DeepClone();
        // Unknown components and any hierarchy are retained: their ownership cannot be inferred.
        if (!keepScenery && !Entities.Any(e => e.HasParent))
        {
            string[] known = ["TransformDefinitionComponent", "ModelDefinitionComponent", "ModelVariationDefinitionComponent", "ModelPlatformTierComponent", "PhysicsBodyDefinitionComponent"];
            var remove = Entities.Where(e => !e.IsTerrain && e.PreviewModel is not null &&
                e.Components.All(c => known.Contains(c["type"]?.GetValue<string>()))).Select(e => e.Index).ToHashSet();
            copy["entities"] = new JsonArray(Entities.Where(e => !remove.Contains(e.Index)).Select(e => e.Data.DeepClone()).ToArray());
        }
        return new(copy, Encoding.UTF8.GetBytes(copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), path);
    }

    public PresetEntity AddModel(string modelPath, Vector3d position, IEnumerable<string>? texturePaths = null)
    {
        static string ValidatePath(string path, string extension)
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or ""))
                throw new ArgumentException("Expected a data/ asset path ending in " + extension);
            return path;
        }
        modelPath = ValidatePath(modelPath, ".model");
        var textures = (texturePaths ?? []).Select(p => ValidatePath(p, ".texture")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        new EntityTransform(position, new(0, 0, 0, 1), new(1, 1, 1)).Validate();
        bool hadDependencies = root.ContainsKey("dependencies");
        var beforeDependencies = root["dependencies"]?.DeepClone();
        if (beforeDependencies is not null && beforeDependencies is not JsonObject)
            throw new InvalidDataException("Expected a dependency object.");
        var dependencies = beforeDependencies?.DeepClone() as JsonObject ?? new JsonObject();
        void AddDependency(string category, string path)
        {
            if (dependencies[category] is not null && dependencies[category] is not JsonArray)
                throw new InvalidDataException("Expected a dependency array: " + category);
            var paths = dependencies[category] as JsonArray ?? new JsonArray();
            if (!paths.OfType<JsonObject>().Any(p => string.Equals(p["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)))
                paths.Add(new JsonObject { ["path"] = path });
            if (dependencies[category] is null) dependencies[category] = paths;
        }
        AddDependency("models", modelPath);
        foreach (var texture in textures) AddDependency("textures", texture);
        var names = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var basename = Path.GetFileNameWithoutExtension(modelPath); string name = basename;
        for (int suffix = 1; names.Contains(name); suffix++) name = basename + "_" + suffix;
        var ids = entities.Select(e => e.Id).ToHashSet();
        uint id;
        do { id = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1); } while (ids.Contains(id.ToString(CultureInfo.InvariantCulture)));
        var data = new JsonObject { ["type"] = "Entity", ["name"] = name, ["id"] = id,
            ["components"] = new JsonArray(
                new JsonObject { ["type"] = "TransformDefinitionComponent", ["name"] = name + "_Transform",
                    ["position"] = new JsonObject { ["x"] = position.X, ["y"] = position.Y, ["z"] = position.Z },
                    ["orientation"] = new JsonObject { ["x"] = 0d, ["y"] = 0d, ["z"] = 0d, ["w"] = 1d },
                    ["scale"] = new JsonObject { ["x"] = 1d, ["y"] = 1d, ["z"] = 1d }, ["inheritOnlyPosition"] = false },
                new JsonObject { ["type"] = "ModelDefinitionComponent", ["name"] = name + "_Model", ["filename"] = modelPath,
                    ["visibleLayers"] = 1, ["lightMask"] = 19, ["shadowMask"] = 3, ["ghostShadows"] = false,
                    ["floorModel"] = false, ["terrainBlendEnableYUpBlend"] = false, ["terrainBlendMode"] = 1 }) };
        var entity = new PresetEntity(data, nextIndex++);
        var array = (JsonArray)root["entities"]!;
        void Insert() { array.Add(data); entities.Add(entity); root["dependencies"] = dependencies.DeepClone(); }
        void Remove()
        {
            array.Remove(data); entities.Remove(entity);
            if (hadDependencies) root["dependencies"] = beforeDependencies?.DeepClone(); else root.Remove("dependencies");
        }
        Insert(); dirtyCache = null;
        History.Record(() => { Remove(); dirtyCache = null; }, () => { Insert(); dirtyCache = null; }, entity);
        return entity;
    }

    public void DeleteModel(PresetEntity entity)
    {
        if (!entities.Contains(entity)) throw new ArgumentException("Entity belongs to another document.");
        if (entity.HasParent || entity.IsTerrain || entity.PreviewModel is null)
            throw new InvalidOperationException("Deletion supports standalone models, not terrain or parented entities.");
        var array = (JsonArray)root["entities"]!;
        int index = entities.IndexOf(entity), nodeIndex = array.IndexOf(entity.Data);
        void Remove() { array.Remove(entity.Data); entities.Remove(entity); dirtyCache = null; }
        void Restore() { array.Insert(nodeIndex, entity.Data); entities.Insert(index, entity); dirtyCache = null; }
        Remove(); History.Record(Restore, Remove, entity);
    }

    public void SetTransform(PresetEntity entity, EntityTransform value)
    {
        if (!Entities.Contains(entity)) throw new ArgumentException("Entity belongs to another document.");
        value.Validate();
        var node = entity.TransformNode ?? throw new InvalidOperationException("No editable transform.");
        if (entity.Transform == value) return;
        var before = (JsonObject)node.DeepClone();
        var after = (JsonObject)node.DeepClone();
        // Patch only changed fields. Preserve unknown vector fields and the exact numeric
        // values of unchanged coordinates, rather than reconstructing the component.
        PatchVector(after, "position", value.Position, entity.Transform.Position);
        PatchVector(after, "scale", value.Scale, entity.Transform.Scale);
        var old = entity.Transform.Orientation;
        var q = value.Orientation;
        if (old != q)
        {
            var orientation = after["orientation"] as JsonObject ?? new JsonObject();
            if (old.X != q.X) orientation["x"] = q.X;
            if (old.Y != q.Y) orientation["y"] = q.Y;
            if (old.Z != q.Z) orientation["z"] = q.Z;
            if (old.W != q.W) orientation["w"] = q.W;
            if (after["orientation"] is null) after["orientation"] = orientation;
        }
        Replace(node, after);
        dirtyCache = null;
        History.Record(() => { Replace(node, before); dirtyCache = null; }, () => { Replace(node, after); dirtyCache = null; }, entity);
    }

    private static void PatchVector(JsonObject parent, string key, Vector3d value, Vector3d old)
    {
        if (value == old) return;
        var node = parent[key] as JsonObject ?? new JsonObject();
        if (value.X != old.X) node["x"] = value.X;
        if (value.Y != old.Y) node["y"] = value.Y;
        if (value.Z != old.Z) node["z"] = value.Z;
        if (parent[key] is null) parent[key] = node;
    }

    private static void Replace(JsonObject target, JsonObject source)
    {
        target.Clear();
        foreach (var pair in source) target[pair.Key] = pair.Value?.DeepClone();
    }

    public PresetEntity? Undo()
    {
        return History.Undo() as PresetEntity;
    }
    public PresetEntity? Redo()
    {
        return History.Redo() as PresetEntity;
    }

    public IEnumerable<string> AssetPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj)
                {
                    if ((key == "path" || key == "filename") && value is JsonValue v &&
                        v.TryGetValue<string>(out var path) && path.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                        paths.Add(path.Replace('\\', '/'));
                    else Visit(value);
                }
            else if (node is JsonArray arr) foreach (var value in arr) Visit(value);
        }
        Visit(root);
        return paths.Order(StringComparer.OrdinalIgnoreCase);
    }

    public byte[] Serialize()
    {
        // A pristine document and edits fully undone retain the original bytes (including BOM/whitespace).
        var original = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes).TrimStart('\uFEFF'));
        if (JsonNode.DeepEquals(root, original)) return originalBytes.ToArray();
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SaveCopy(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save copy requires a different file from the source preset. Use Save Scene for workspace edits.");
        var bytes = Serialize();
        var reparsed = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (!JsonNode.DeepEquals(root, reparsed)) throw new InvalidDataException("JSON round-trip verification failed.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
            savedJson = root.ToJsonString();
            dirtyCache = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void MarkSaved() { savedJson = root.ToJsonString(); dirtyCache = false; }
}
