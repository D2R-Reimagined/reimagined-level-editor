using D2RLevel.Core;
using LSLib.Granny.GR2;
using LSLib.Granny.Model;

namespace D2RLevel.Assets;

public sealed record MeshPart(string Name, float[] Positions, float[] Normals, float[] UVs, int[] Indices, string? AlbedoPath);
public sealed record ModelAsset(IReadOnlyList<MeshPart> Parts, IReadOnlyList<string>? TexturePaths = null);
public sealed class MissingModelDecoderException : NotSupportedException
{
    public MissingModelDecoderException() : base("Granny decoder is not configured. Choose Granny decoder… and select a 64-bit granny2.dll to render compressed models.") { }
}

public static class ModelReader
{
    public static bool IsDecoderConfigured => LSLib.Native.Granny2Compressor.IsConfigured;
    public static void ConfigureDecoder(string path) => LSLib.Native.Granny2Compressor.Configure(path);

    public static ModelAsset Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Model is not extracted.", path);
        if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Model exceeds the editor's 128 MiB limit.");
        using var stream = new MemoryStream(File.ReadAllBytes(path), false);
        ValidateSections(stream);
        using var reader = new GR2Reader(stream);
        var root = new D2rModelRoot();
        reader.Read(root);
        var parts = new List<MeshPart>();
        foreach (var mesh in root.Meshes ?? [])
        {
            var vertices = mesh.PrimaryVertexData?.Vertices;
            var topology = mesh.PrimaryTopology;
            if (vertices is null || topology is null || vertices.Count == 0) continue;
            var indices = topology.Indices is { Count: > 0 } ? topology.Indices.ToArray() : topology.Indices16?.Select(i => (int)i).ToArray() ?? [];
            if (indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= vertices.Count))
                throw new InvalidDataException($"Invalid triangle indices in {mesh.Name}.");
            // D2R stores normalized vertex coordinates with a per-mesh scale. Omitting
            // this produces individually recognizable meshes but a nearly empty town.
            var vertexScale = mesh.ExtendedData?.VertexScale ?? 1;
            if (!float.IsFinite(vertexScale) || vertexScale <= 0)
                throw new InvalidDataException($"Invalid vertex scale in {mesh.Name}.");
            var positions = vertices.SelectMany(v => new[] { v.Position.X * vertexScale, v.Position.Y * vertexScale, v.Position.Z * vertexScale }).ToArray();
            var normals = vertices.SelectMany(v => new[] { v.Normal.X, v.Normal.Y, v.Normal.Z }).ToArray();
            var uvs = vertices.SelectMany(v => new[] { v.TextureCoordinates0.X, v.TextureCoordinates0.Y }).ToArray();
            if (positions.Concat(normals).Concat(uvs).Any(v => !float.IsFinite(v)))
                throw new InvalidDataException($"Non-finite vertex data in {mesh.Name}.");
            var groups = topology.Groups is { Count: > 0 } ? topology.Groups :
                [new TriTopologyGroup { MaterialIndex = 0, TriFirst = 0, TriCount = indices.Length / 3 }];
            foreach (var group in groups)
            {
                var start = checked(group.TriFirst * 3);
                var count = checked(group.TriCount * 3);
                if (start < 0 || count < 0 || (long)start + count > indices.Length)
                    throw new InvalidDataException("Invalid material triangle range.");
                var material = group.MaterialIndex >= 0 && group.MaterialIndex < (mesh.MaterialBindings?.Count ?? 0)
                    ? mesh.MaterialBindings![group.MaterialIndex].Material : null;
                parts.Add(new(mesh.Name, positions, normals, uvs, indices.AsSpan(start, count).ToArray(), Albedo(material)));
            }
        }
        if (parts.Count == 0) throw new InvalidDataException("No renderable meshes found.");
        var texturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Material>();
        void Visit(Material? material)
        {
            if (material is null || !visited.Add(material)) return;
            if (material.Texture?.FromFileName is { Length: > 0 } path) texturePaths.Add(path.Replace('\\', '/'));
            foreach (var map in material.Maps ?? []) Visit(map.Map);
        }
        foreach (var mesh in root.Meshes ?? []) foreach (var binding in mesh.MaterialBindings ?? []) Visit(binding.Material);
        return new(parts, texturePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string? Albedo(Material? material)
    {
        var map = material?.Maps?.FirstOrDefault(m => m.Usage.Contains("diffuse", StringComparison.OrdinalIgnoreCase) ||
            m.Usage.Contains("albedo", StringComparison.OrdinalIgnoreCase) || m.Usage.Contains("basecolor", StringComparison.OrdinalIgnoreCase));
        return map?.Map?.Texture?.FromFileName ?? material?.Texture?.FromFileName;
    }

    private static void ValidateSections(Stream stream)
    {
        // Bound allocations before handing the file to the upstream reader.
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (stream.Length < 104) throw new InvalidDataException("Truncated GR2 header.");
        stream.Position = 32;
        var version = r.ReadUInt32();
        var length = r.ReadUInt32();
        r.ReadUInt32();
        var sectionOffset = r.ReadUInt32();
        var count = r.ReadUInt32();
        if (version is not (6 or 7) || length > stream.Length || count > 64 || sectionOffset != (version == 7 ? 72 : 56))
            throw new InvalidDataException("Unsupported or invalid GR2 header.");
        stream.Position = 32 + sectionOffset;
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var compression = r.ReadUInt32();
            var offset = r.ReadUInt32();
            var compressed = r.ReadUInt32();
            var size = r.ReadUInt32();
            if ((long)offset + compressed > stream.Length || (total += size) > 512L * 1024 * 1024)
                throw new InvalidDataException("Invalid or oversized GR2 section.");
            if (compression is not (0 or 1 or 2 or 4)) throw new NotSupportedException($"Unknown GR2 compression {compression}.");
            if (compression != 0 && !IsDecoderConfigured) throw new MissingModelDecoderException();
            stream.Position += 28;
        }
        stream.Position = 0;
    }
}

// Current upstream LSLib's default Mesh DTO is tailored to Divinity and drops
// D2R's VertexScale. Use D2R-specific reflection DTOs with its generic GR2 reader.
public sealed class D2rModelRoot
{
    [Serialization(Type = MemberType.ArrayOfReferences)]
    public List<D2rMesh>? Meshes;
}
public sealed class D2rMesh
{
    public string Name = "";
    public VertexData? PrimaryVertexData;
    public TriTopology? PrimaryTopology;
    [Serialization(DataArea = true)]
    public List<MaterialBinding>? MaterialBindings;
    [Serialization(Type = MemberType.VariantReference)]
    public D2rMeshData? ExtendedData;
}
public sealed class D2rMeshData
{
    public float VertexScale = 1;
}
