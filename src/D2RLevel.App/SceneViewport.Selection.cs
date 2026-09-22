using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class SceneViewport
{
    public event Action<PresetEntity[], ModifierKeys>? RegionSelected;
    private readonly SelectionMarquee marquee = new() { IsHitTestVisible = false };
    private bool marqueePending, marqueeDragging;
    private Point marqueeStart;
    private PresetEntity? marqueeClick;
    private ModifierKeys marqueeModifiers;
    internal Rect? SelectionRectangle => marquee.Rectangle;

    internal bool BeginLeftInteraction(Point point, ModifierKeys modifiers)
    {
        if (HitGizmo(point) is not null)
            return TryBeginGizmoDrag(point, modifiers.HasFlag(ModifierKeys.Alt));
        var entity = Pick(point);
        if (RegionSelected is not null && (entity is null || (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0))
        {
            CancelDrag();
            marqueeStart = point; marqueeClick = entity; marqueeModifiers = modifiers;
            marqueePending = true; marqueeDragging = false;
            return true;
        }
        if (entity is null) return false;
        EntitySelected?.Invoke(entity);
        return (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 && BeginDrag(entity, point);
    }

    internal void ContinueMarquee(Point point)
    {
        if (!marqueePending) return;
        point = new(Math.Clamp(point.X, 0, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight));
        if (!marqueeDragging && Math.Abs(point.X - marqueeStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - marqueeStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        marqueeDragging = true;
        marquee.Rectangle = new Rect(marqueeStart, point); marquee.InvalidateVisual();
        Cursor = Cursors.Cross;
    }

    internal void EndMarquee(bool commit)
    {
        if (!marqueePending) return;
        var matches = commit ? marqueeDragging && marquee.Rectangle is { } rectangle ? PickRegion(rectangle)
            : marqueeClick is { } clicked ? [clicked] : Array.Empty<PresetEntity>() : [];
        var modifiers = marqueeModifiers;
        var click = !marqueeDragging ? marqueeClick : null;
        marqueePending = marqueeDragging = false; marqueeClick = null;
        marquee.Rectangle = null; marquee.InvalidateVisual(); Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (commit && click is not null) EntitySelected?.Invoke(click);
        else if (commit) RegionSelected?.Invoke(matches, modifiers);
    }

    internal PresetEntity[] PickRegion(Rect rectangle)
    {
        rectangle.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
        if (rectangle.IsEmpty || rectangle.Width == 0 || rectangle.Height == 0) return [];
        return visuals.Where(pair =>
        {
            var visual = pair.Value; var entity = entities[visual];
            if (IsLocked(entity) || (entity.IsTerrain && !terrainVisible)) return false;
            bool batched = entityBatches.TryGetValue(entity.Index, out var batches);
            if (batched ? !batches!.Any(b => batchVisuals[b].Content is not null) : !viewport.Children.Contains(visual)) return false;
            var bounds = visual.Transform.TransformBounds(visual.Content.Bounds);
            if (bounds.IsEmpty) return false;
            if (batched && workRadius is { } radius)
            {
                var closest = new Point3D(Math.Clamp(workCenter.X, bounds.X, bounds.X + bounds.SizeX),
                    Math.Clamp(workCenter.Y, bounds.Y, bounds.Y + bounds.SizeY), Math.Clamp(workCenter.Z, bounds.Z, bounds.Z + bounds.SizeZ));
                if ((closest - workCenter).LengthSquared > radius * radius) return false;
            }
            return ProjectBounds(bounds) is { } screenBounds && rectangle.IntersectsWith(screenBounds);
        }).Select(pair => entities[pair.Value]).ToArray();
    }

    // Clip box edges against the camera's near plane before projecting. A box entirely
    // behind the camera must not acquire a mirrored selection rectangle on screen.
    internal Rect? ProjectBounds(Rect3D bounds)
    {
        if (bounds.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0) return null;
        var forward = camera.LookDirection; forward.Normalize();
        double near = camera.NearPlaneDistance * 1.001;
        var corners = new Point3D[8]; var depths = new double[8];
        var result = Rect.Empty;
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new(bounds.X + ((i & 1) == 0 ? 0 : bounds.SizeX),
                bounds.Y + ((i & 2) == 0 ? 0 : bounds.SizeY), bounds.Z + ((i & 4) == 0 ? 0 : bounds.SizeZ));
            depths[i] = Vector3D.DotProduct(corners[i] - camera.Position, forward);
            if (depths[i] >= near && Project(corners[i]) is { } point) result.Union(point);
        }
        for (int i = 0; i < 8; i++)
            foreach (int bit in new[] { 1, 2, 4 })
            {
                int j = i ^ bit;
                if (j <= i || (depths[i] >= near) == (depths[j] >= near)) continue;
                var clipped = corners[i] + (corners[j] - corners[i]) * ((near - depths[i]) / (depths[j] - depths[i]));
                if (Project(clipped) is { } point) result.Union(point);
            }
        result.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
        return result.IsEmpty ? null : result;
    }
}

internal sealed class SelectionMarquee : FrameworkElement
{
    internal Rect? Rectangle { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        if (Rectangle is not { } rectangle) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 71, 207, 215)),
            new Pen(Brushes.Turquoise, 1.5) { DashStyle = DashStyles.Dash }, rectangle);
    }
}
