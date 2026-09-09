using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

// Only the render representation is combined. Entity JSON and source geometry stay separate.
public sealed class SceneBatch
{
    public sealed record Part(PresetEntity Entity, GeometryModel3D Model);
    public IReadOnlyList<Part> Parts { get; }
    public bool IsTerrain { get; }
    private (int End, PresetEntity Entity)[] ranges = [];
    private readonly HashSet<PresetEntity> removed = new();
    public void SetIncluded(PresetEntity entity, bool included) { if (included) removed.Remove(entity); else removed.Add(entity); }
    public GeometryModel3D Model { get; private set; }
    public SceneBatch(IReadOnlyList<Part> parts, CancellationToken token = default)
    {
        Parts = parts;
        IsTerrain = parts[0].Entity.IsTerrain;
        Model = Build(token);
    }
    public PresetEntity? EntityAt(int vertex) => vertex < 0 ? null : ranges.FirstOrDefault(r => vertex < r.End).Entity;
    public void Rebuild(PresetEntity? exclude = null) => Model = Build(exclude: exclude);
    private GeometryModel3D Build(CancellationToken token = default, PresetEntity? exclude = null)
    {
        var positions = new Point3DCollection(); var normals = new Vector3DCollection();
        var uv = new PointCollection(); var indices = new Int32Collection();
        var included = Parts.Where(p => p.Entity != exclude && !removed.Contains(p.Entity)).ToArray();
        int end = 0;
        ranges = included.Select(p => (end += ((MeshGeometry3D)p.Model.Geometry).Positions.Count, p.Entity)).ToArray();
        foreach (var part in included)
        {
            token.ThrowIfCancellationRequested();
            var source = (MeshGeometry3D)part.Model.Geometry;
            int offset = positions.Count;
            var matrix = SceneViewport.Transform(part.Entity.Transform).Value;
            var inverse = matrix;
            if (inverse.HasInverse) inverse.Invert(); else inverse = Matrix3D.Identity;
            bool flip = matrix.Determinant < 0;
            foreach (var p in source.Positions) positions.Add(matrix.Transform(p));
            foreach (var n in source.Normals)
            {
                var normal = new Vector3D(n.X * inverse.M11 + n.Y * inverse.M12 + n.Z * inverse.M13,
                    n.X * inverse.M21 + n.Y * inverse.M22 + n.Z * inverse.M23,
                    n.X * inverse.M31 + n.Y * inverse.M32 + n.Z * inverse.M33);
                if (normal.LengthSquared > 0) normal.Normalize(); normals.Add(normal);
            }
            foreach (var p in source.TextureCoordinates) uv.Add(p);
            for (int i = 0; i < source.TriangleIndices.Count; i += 3)
            {
                indices.Add(offset + source.TriangleIndices[i]);
                indices.Add(offset + source.TriangleIndices[i + (flip ? 2 : 1)]);
                indices.Add(offset + source.TriangleIndices[i + (flip ? 1 : 2)]);
            }
        }
        var model = new GeometryModel3D(new MeshGeometry3D { Positions = positions, Normals = normals, TextureCoordinates = uv, TriangleIndices = indices }, Parts[0].Model.Material)
            { BackMaterial = Parts[0].Model.BackMaterial };
        model.Freeze(); return model;
    }

    public static SceneBatch[] Create(IReadOnlyList<SceneItem> items, CancellationToken token)
    {
        // Bound both spatial extent and vertex count so a small edit rebuilds only local batches.
        var groups = new Dictionary<(Material, Material?, int, int, bool), List<Part>>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            foreach (GeometryModel3D part in item.Geometry.Children)
            {
                var position = item.Entity.Transform.Position;
                var key = (part.Material, part.BackMaterial, (int)Math.Floor(position.X / 128), (int)Math.Floor(position.Z / 128), item.Entity.IsTerrain);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                group.Add(new(item.Entity, part));
            }
        }
        var result = new List<SceneBatch>();
        foreach (var group in groups.Values)
        {
            var chunk = new List<Part>(); int vertices = 0;
            foreach (var part in group)
            {
                token.ThrowIfCancellationRequested();
                int count = ((MeshGeometry3D)part.Model.Geometry).Positions.Count;
                if (vertices + count > 32000 && chunk.Count > 0) { result.Add(new(chunk.ToArray(), token)); chunk.Clear(); vertices = 0; }
                chunk.Add(part); vertices += count;
            }
            if (chunk.Count > 0) result.Add(new(chunk.ToArray(), token));
        }
        return result.ToArray();
    }
}
