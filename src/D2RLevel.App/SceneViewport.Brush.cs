using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>A model to paint into the scene: its geometry, the orientation each piece gets, and the grid it snaps to (0 for none).</summary>
public sealed record ModelBrush(string ModelPath, Model3DGroup Geometry, Quaterniond Orientation, double Snap);

/// <summary>
/// Painting with a <see cref="ModelBrush"/>: a ghost piece follows the pointer over the ground, a click places one, and a drag lays
/// a straight row along X or Z, one piece per model width (whole grid steps when snapping). R turns the brush 90°; Esc ends a
/// stroke, or painting when no stroke is under way. Right and middle drags still look and pan.
/// </summary>
public sealed partial class SceneViewport
{
    public event Action<EntityTransform[]>? BrushCommitted;
    /// <summary>R turned the brush; the new brush is passed so its owner can follow.</summary>
    public event Action<ModelBrush>? BrushRotated;
    /// <summary>Esc with no stroke under way: the owner should stop painting.</summary>
    public event Action? BrushCanceled;
    /// <summary>Ground height under a point, so pieces sit on the terrain rather than at zero.</summary>
    public Func<double, double, double>? GroundHeight { get; set; }
    private ModelBrush? brush;
    private readonly ModelVisual3D brushPreview = new();
    private readonly TextBlock brushLabel = new()
    {
        Margin = new Thickness(12), Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(225, 19, 26, 35)),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private bool brushStroke;
    private Point3D brushStart;
    /// <summary>Where the current stroke, or the hovering ghost, would place pieces.</summary>
    internal IReadOnlyList<EntityTransform> BrushPlan { get; private set; } = [];
    internal bool BrushStrokeActive => brushStroke;

    public ModelBrush? Brush
    {
        get => brush;
        set
        {
            CancelDrag(); brush = value; BrushPlan = [];
            if (value is null)
            {
                brushPreview.Content = null; viewport.Children.Remove(brushPreview);
                brushLabel.Visibility = Visibility.Collapsed; Cursor = null;
            }
            else
            {
                if (!viewport.Children.Contains(brushPreview)) viewport.Children.Add(brushPreview);
                brushLabel.Text = L.T("Painting: click to place · drag for a row · R rotates · Esc stops");
                brushLabel.Visibility = Visibility.Visible; Cursor = Cursors.Cross;
                if (IsMouseOver) UpdateBrush(Mouse.GetPosition(this));
            }
            UpdateGizmo();
        }
    }

    /// <summary>The model's extent along world X and Z once turned to the brush orientation.</summary>
    internal (double X, double Z) BrushSize()
    {
        if (brush is null) return (0, 0);
        var bounds = Transform(new(new(0, 0, 0), brush.Orientation, new(1, 1, 1))).TransformBounds(brush.Geometry.Bounds);
        return bounds.IsEmpty ? (0, 0) : (bounds.SizeX, bounds.SizeZ);
    }

    /// <summary>The ground point under the pointer, snapped, at the terrain's height there.</summary>
    private Point3D? BrushPoint(Point point)
    {
        if (brush is null || GroundPoint(point, 0) is not { } flat) return null;
        // Terrain is rarely at zero: intersect again at the height found under the first hit.
        double height = GroundHeight?.Invoke(flat.X, flat.Z) ?? 0;
        if (!double.IsFinite(height)) height = 0;
        var ground = height == 0 ? flat : GroundPoint(point, height) ?? flat;
        double x = ground.X, z = ground.Z;
        if (brush.Snap > 0) { x = Math.Round(x / brush.Snap) * brush.Snap; z = Math.Round(z / brush.Snap) * brush.Snap; }
        return new(x, GroundHeight?.Invoke(x, z) is { } y && double.IsFinite(y) ? y : height, z);
    }

    /// <summary>Recompute the plan for the pointer: one ghost while hovering, a row while a stroke is under way.</summary>
    internal void UpdateBrush(Point point)
    {
        if (brush is null) return;
        if (BrushPoint(point) is not { } at) { BrushPlan = []; brushPreview.Content = null; return; }
        var start = brushStroke ? brushStart : at;
        var delta = at - start;
        var (sizeX, sizeZ) = BrushSize();
        bool alongX = Math.Abs(delta.X) >= Math.Abs(delta.Z);
        double spacing = Math.Max(alongX ? sizeX : sizeZ, 0.0001);
        if (brush.Snap > 0) spacing = Math.Max(brush.Snap, Math.Round(spacing / brush.Snap) * brush.Snap);
        double travel = alongX ? delta.X : delta.Z;
        int count = !brushStroke ? 1 : (int)Math.Min(PresetDocument.MaximumDuplicateModels, Math.Floor(Math.Abs(travel) / spacing + .5) + 1);
        var plan = new EntityTransform[count];
        for (int i = 0; i < count; i++)
        {
            double offset = Math.Sign(travel) * spacing * i;
            plan[i] = new(new(start.X + (alongX ? offset : 0), start.Y, start.Z + (alongX ? 0 : offset)), brush.Orientation, new(1, 1, 1));
        }
        BrushPlan = plan;
        var group = new Model3DGroup();
        foreach (var t in plan) group.Children.Add(new Model3DGroup { Children = { brush.Geometry }, Transform = Transform(t) });
        group.Freeze(); brushPreview.Content = group;
        if (brushStroke) brushLabel.Text = L.T("{0} pieces. Release to place; Esc cancels.", count);
    }

    internal bool BeginBrush(Point point)
    {
        if (brush is null || BrushPoint(point) is not { } at) return false;
        brushStroke = true; brushStart = at; UpdateBrush(point);
        return true;
    }

    internal void EndBrush(bool commit)
    {
        if (!brushStroke) return;
        var plan = BrushPlan.ToArray();
        brushStroke = false;
        if (brush is not null) brushLabel.Text = L.T("Painting: click to place · drag for a row · R rotates · Esc stops");
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (commit && plan.Length > 0) BrushCommitted?.Invoke(plan);
        if (brush is not null && IsMouseOver) UpdateBrush(Mouse.GetPosition(this)); else { BrushPlan = []; brushPreview.Content = null; }
    }

    /// <summary>Turn the brush a quarter turn about the vertical axis.</summary>
    internal void RotateBrush()
    {
        if (brush is null) return;
        var q = brush.Orientation;
        var turned = new Quaternion(new Vector3D(0, 1, 0), 90) * new Quaternion(q.X, q.Y, q.Z, q.W);
        turned.Normalize();
        brush = brush with { Orientation = new(turned.X, turned.Y, turned.Z, turned.W) };
        BrushRotated?.Invoke(brush);
        if (IsMouseOver) UpdateBrush(Mouse.GetPosition(this));
    }

    /// <summary>Esc: abandon the stroke, or stop painting when there is none.</summary>
    private bool BrushEscape()
    {
        if (brush is null) return false;
        if (brushStroke) EndBrush(false); else BrushCanceled?.Invoke();
        return true;
    }
}
