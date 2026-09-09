using System.Windows.Media.Media3D;
namespace D2RLevel.App;

// Terrain-only height lookup. Spatial buckets avoid scanning the entire map on every DS1 edit.
internal sealed class NpcGround
{
    private readonly Dictionary<(int, int), List<(Point3D A, Point3D B, Point3D C)>> cells = new();
    public NpcGround(IEnumerable<SceneItem> items, CancellationToken token)
    {
        foreach (var item in items.Where(i => i.Entity.IsTerrain && !i.IsPlaceholder))
            Visit(item.Geometry, SceneViewport.Transform(item.Entity.Transform).Value);
        void Visit(Model3D model, Matrix3D parent)
        {
            token.ThrowIfCancellationRequested();
            var matrix = model.Transform.Value; matrix.Append(parent);
            if (model is Model3DGroup group) { foreach (var child in group.Children) Visit(child, matrix); return; }
            if (model is not GeometryModel3D { Geometry: MeshGeometry3D mesh }) return;
            var positions = mesh.Positions.Select(matrix.Transform).ToArray();
            for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                var a = positions[mesh.TriangleIndices[i]]; var b = positions[mesh.TriangleIndices[i + 1]]; var c = positions[mesh.TriangleIndices[i + 2]];
                int x0 = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / 10), x1 = (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / 10);
                int z0 = (int)Math.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / 10), z1 = (int)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / 10);
                if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 100000) continue;
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                { if (!cells.TryGetValue((x,z), out var bucket)) cells[(x,z)] = bucket = new(); bucket.Add((a,b,c)); }
            }
        }
    }
    public double Height(double x, double z)
    {
        double? height = null;
        if (cells.TryGetValue(((int)Math.Floor(x / 10), (int)Math.Floor(z / 10)), out var triangles))
            foreach (var (a,b,c) in triangles)
            {
                double denominator = (b.Z-c.Z)*(a.X-c.X)+(c.X-b.X)*(a.Z-c.Z);
                if (Math.Abs(denominator) < 1e-10) continue;
                double u = ((b.Z-c.Z)*(x-c.X)+(c.X-b.X)*(z-c.Z))/denominator;
                double v = ((c.Z-a.Z)*(x-c.X)+(a.X-c.X)*(z-c.Z))/denominator;
                if (u < -1e-7 || v < -1e-7 || u+v > 1.0000001) continue;
                double y = u*a.Y+v*b.Y+(1-u-v)*c.Y;
                height = height is null ? y : Math.Max(height.Value,y);
            }
        return height ?? 0;
    }
}
