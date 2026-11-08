using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace D2RLevel.App;

/// <summary>One flat area of a resize preview in HD ground coordinates (X and Z).</summary>
public sealed record GroundPatch(Rect Area, Color Color);

/// <summary>
/// The resize preview: translucent patches laid over the terrain for the tiles a resize adds or removes, and an
/// outline along the new map edge, so the change can be judged against what the level actually shows.
/// </summary>
public sealed partial class SceneViewport
{
    private readonly ModelVisual3D resizePreview = new();
    internal bool HasResizePreview => resizePreview.Content is not null;

    /// <param name="height">Ground height under an HD point; patches drape over it so hills do not hide them.</param>
    /// <param name="step">Sampling spacing, normally one tile.</param>
    public void SetResizePreview(IReadOnlyList<GroundPatch> patches, Rect? edge, Func<double, double, double>? height, double step)
    {
        viewport.Children.Remove(resizePreview);
        if (patches.Count == 0 && edge is null) { resizePreview.Content = null; return; }
        double lift = step * 0.06;
        double Y(double x, double z) => (height?.Invoke(x, z) is { } y && double.IsFinite(y) ? y : 0) + lift;
        var group = new Model3DGroup();
        foreach (var patch in patches)
        {
            var r = patch.Area; if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
            // At most 128 samples a side keeps a whole-map strip cheap; a tile is the finest useful step.
            int nx = Math.Clamp((int)Math.Ceiling(r.Width / step), 1, 128), nz = Math.Clamp((int)Math.Ceiling(r.Height / step), 1, 128);
            var mesh = new MeshGeometry3D();
            for (int j = 0; j <= nz; j++) for (int i = 0; i <= nx; i++)
            {
                double x = r.X + r.Width * i / nx, z = r.Y + r.Height * j / nz;
                mesh.Positions.Add(new(x, Y(x, z), z));
            }
            for (int j = 0; j < nz; j++) for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
            var material = new MaterialGroup
            {
                Children =
                {
                    new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(patch.Color.A, patch.Color.R, patch.Color.G, patch.Color.B))),
                    new EmissiveMaterial(new SolidColorBrush(Color.FromArgb((byte)(patch.Color.A / 2), patch.Color.R, patch.Color.G, patch.Color.B))),
                },
            };
            material.Freeze();
            group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }
        if (edge is { IsEmpty: false } e)
        {
            var line = new MeshGeometry3D();
            var corners = new[] { new Point(e.Left, e.Top), new Point(e.Right, e.Top), new Point(e.Right, e.Bottom), new Point(e.Left, e.Bottom) };
            for (int c = 0; c < 4; c++)
            {
                Point from = corners[c], to = corners[(c + 1) % 4];
                int n = Math.Clamp((int)Math.Ceiling((to - from).Length / step), 1, 256);
                for (int i = 0; i < n; i++)
                {
                    Point a = from + (to - from) * i / n, b = from + (to - from) * (i + 1) / n;
                    AddTube(line, new(a.X, Y(a.X, a.Y) + lift, a.Y), new(b.X, Y(b.X, b.Y) + lift, b.Y), step * 0.06);
                }
            }
            var material = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(255, 232, 140))); material.Freeze();
            group.Children.Add(new GeometryModel3D(line, material) { BackMaterial = material });
        }
        group.Freeze();
        resizePreview.Content = group;
        // Last, so the translucent patches blend over everything already drawn.
        viewport.Children.Add(resizePreview);
    }

    public void ClearResizePreview() { viewport.Children.Remove(resizePreview); resizePreview.Content = null; }
}
