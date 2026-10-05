using System.Buffers.Binary;
using System.Numerics;

namespace D2RLevel.Assets;

/// <summary>Conservative read-only bounds for the packed triangle physics layout.
/// Never rewrites its opaque tree, adjacency or material records.</summary>
public sealed class PhysicsMeshBounds
{
    private readonly Vector3[] vertices;
    private readonly int[] indices;
    private PhysicsMeshBounds(Vector3[] vertices, int[] indices) { this.vertices = vertices; this.indices = indices; }

    public static PhysicsMeshBounds Read(byte[] bytes)
    {
        if (bytes.Length < 80) throw new InvalidDataException("Truncated physics mesh.");
        long Offset(int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
        int Count(int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        int nodes = Count(24), count = Count(28), triangles = Count(32);
        if (nodes <= 0 || count < 3 || triangles <= 0 || Offset(0) != 48 ||
            Offset(8) != 48L + nodes * 32L || Offset(16) != Offset(8) + count * 16L ||
            bytes.LongLength != Offset(16) + triangles * 32L)
            throw new NotSupportedException("Unsupported packed physics mesh layout.");
        float Float(int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
        Vector3 Point(int at) => new(Float(at), Float(at + 4), Float(at + 8));
        var min = Point(48); var max = Point(60);
        bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
        if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException("Invalid physics root bounds.");
        var vertices = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var p = vertices[i] = Point(checked((int)Offset(8) + i * 16));
            if (!Finite(p) || p.X < min.X || p.Y < min.Y || p.Z < min.Z || p.X > max.X || p.Y > max.Y || p.Z > max.Z)
                throw new InvalidDataException("Physics vertex escapes its root bounds.");
        }
        var indices = new int[checked(triangles * 3)];
        for (int i = 0; i < triangles; i++) for (int k = 0; k < 3; k++)
        {
            int index = Count(checked((int)Offset(16) + i * 32 + k * 4));
            if (index < 0 || index >= count) throw new InvalidDataException("Invalid physics triangle index.");
            indices[i * 3 + k] = index;
        }
        return new(vertices, indices);
    }

    public Vector3[] WorldVertices(Matrix4x4 world)
    {
        var points = vertices.Select(p => Vector3.Transform(p, world)).ToArray();
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new InvalidDataException("Nonfinite physics transform.");
        return points;
    }

    public void RequireFloorBelow(TerrainRectangle area)
    {
        for (int i = 0; i < indices.Length; i += 3)
        {
            var points = new[] { vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]] };
            if (points.Max(p => p.X) <= area.MinX || points.Min(p => p.X) >= area.MaxX ||
                points.Max(p => p.Z) <= area.MinZ || points.Min(p => p.Z) >= area.MaxZ) continue;
            // Conservative triangle bounds can reject a safe selection; they must
            // never accept retained physics protruding through the new floor.
            if (points.Max(p => p.Y) > area.Height + .002f)
                throw new NotSupportedException("Existing terrain physics rises above the selected floor height.");
        }
    }
}
