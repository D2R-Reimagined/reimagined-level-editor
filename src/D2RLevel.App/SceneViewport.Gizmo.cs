using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal enum MovementAxis { X, Y, Z }

public sealed partial class SceneViewport
{
    private readonly MovementGizmo gizmo = new() { IsHitTestVisible = false };
    private MovementAxis? dragAxis;
    private Point3D dragAxisOrigin;
    private Vector3D dragPlaneNormal;

    private bool CanDrag(PresetEntity entity)
    {
        if (!AllowModelDragging || !entity.CanTransform || entity.HasParent || IsLocked(entity) ||
            (entity.IsTerrain && !terrainVisible) || !visuals.ContainsKey(entity.Index)) return false;
        return !selectedMembers.Contains(entity) || selectedMembers.Length <= 1 ||
            selectedMembers.All(e => GroupMovement.CanGroup(e) && !IsLocked(e) && visuals.ContainsKey(e.Index));
    }

    private static Vector3D AxisVector(MovementAxis axis) => axis switch
    {
        MovementAxis.X => new(1, 0, 0), MovementAxis.Y => new(0, 1, 0), _ => new(0, 0, 1)
    };

    private Vector3D PointerRay(Point point)
    {
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double tangent = Math.Tan(camera.FieldOfView * Math.PI / 360);
        return forward + right * ((2 * point.X / ActualWidth - 1) * tangent) +
            up * ((1 - 2 * point.Y / ActualHeight) * tangent * ActualHeight / ActualWidth);
    }

    private Point3D? AxisPlanePoint(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var ray = PointerRay(point);
        double divisor = Vector3D.DotProduct(ray, dragPlaneNormal);
        if (Math.Abs(divisor) < 0.0001) return null;
        double t = Vector3D.DotProduct(dragAxisOrigin - camera.Position, dragPlaneNormal) / divisor;
        if (!double.IsFinite(t) || t <= 0) return null;
        return camera.Position + ray * t;
    }

    internal Point? Project(Point3D point)
    {
        var forward = camera.LookDirection; forward.Normalize();
        var delta = point - camera.Position;
        double depth = Vector3D.DotProduct(delta, forward);
        if (depth <= camera.NearPlaneDistance || !double.IsFinite(depth)) return null;
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double scale = ActualWidth / (2 * Math.Tan(camera.FieldOfView * Math.PI / 360) * depth);
        return new(ActualWidth / 2 + Vector3D.DotProduct(delta, right) * scale,
            ActualHeight / 2 - Vector3D.DotProduct(delta, up) * scale);
    }

    private void UpdateGizmo()
    {
        gizmo.Handles.Clear();
        gizmo.Highlight = dragAxis;
        if (selected is { } entity && CanDrag(entity) && ActualWidth > 0 && ActualHeight > 0)
        {
            var visual = visuals[entity.Index];
            var bounds = visual.Transform.TransformBounds(visual.Content.Bounds);
            if (!bounds.IsEmpty)
            {
                var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
                if (duplicating) center += new Vector3D(duplicateStep.X, duplicateStep.Y, duplicateStep.Z) * duplicateCount;
                if (Project(center) is { } origin)
                {
                    var forward = camera.LookDirection; forward.Normalize();
                    double depth = Vector3D.DotProduct(center - camera.Position, forward);
                    // Constant screen size as the camera travels; world directions remain fixed.
                    double length = 90 * 2 * Math.Tan(camera.FieldOfView * Math.PI / 360) * depth / ActualWidth;
                    foreach (var axis in Enum.GetValues<MovementAxis>())
                    {
                        if (axis == MovementAxis.Y && entity.GameplayUnitIndex is not null) continue;
                        var vector = AxisVector(axis);
                        double alignment = Vector3D.DotProduct(forward, vector);
                        if (1 - alignment * alignment < 0.01) continue;
                        if (Project(center + vector * length) is not { } end) continue;
                        var direction = end - origin;
                        if (direction.Length < 16) continue;
                        direction.Normalize();
                        gizmo.Handles.Add(new(axis, origin + direction * 12, end));
                    }
                }
            }
        }
        gizmo.InvalidateVisual();
    }

    internal MovementAxis? HitGizmo(Point point)
    {
        MovementAxis? nearest = null;
        double best = 9;
        foreach (var handle in gizmo.Handles)
        {
            var segment = handle.End - handle.Start;
            double t = Math.Clamp(Vector.Multiply(point - handle.Start, segment) / segment.LengthSquared, 0, 1);
            double distance = (point - (handle.Start + segment * t)).Length;
            if (distance < best) { nearest = handle.Axis; best = distance; }
        }
        return nearest;
    }

    internal bool TryBeginGizmoDrag(Point point, bool duplicate = false) => selected is { } entity && HitGizmo(point) is { } axis && BeginDrag(entity, point, axis, duplicate);
    internal IReadOnlyList<MovementHandle> MovementHandles => gizmo.Handles;
}

internal sealed record MovementHandle(MovementAxis Axis, Point Start, Point End);

// A projected overlay keeps the handles visible and pickable even inside a model.
internal sealed class MovementGizmo : FrameworkElement
{
    internal List<MovementHandle> Handles { get; } = [];
    internal MovementAxis? Highlight { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var handle in Handles.OrderBy(h => h.Axis == Highlight))
        {
            var brush = handle.Axis switch
            {
                MovementAxis.X => Brushes.Tomato, MovementAxis.Y => Brushes.LimeGreen, _ => Brushes.DodgerBlue
            };
            bool active = handle.Axis == Highlight;
            var direction = handle.End - handle.Start; direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            var neck = handle.End - direction * 13;
            var outline = new Pen(active ? Brushes.White : Brushes.Black, active ? 7 : 6);
            dc.DrawLine(outline, handle.Start, neck);
            dc.DrawLine(new Pen(brush, active ? 4 : 3), handle.Start, neck);
            var arrow = new StreamGeometry();
            using (var context = arrow.Open())
            {
                context.BeginFigure(handle.End, true, true);
                context.LineTo(neck + normal * 6, true, false);
                context.LineTo(neck - normal * 6, true, false);
            }
            dc.DrawGeometry(brush, new Pen(active ? Brushes.White : Brushes.Black, 1.5), arrow);
            var label = new FormattedText(handle.Axis.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 13, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var labelPosition = handle.End + direction * 9 - new Vector(label.Width / 2, label.Height / 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 19, 26, 35)), null,
                new Rect(labelPosition - new Vector(3, 1), new Size(label.Width + 6, label.Height + 2)), 3, 3);
            dc.DrawText(label, labelPosition);
        }
    }
}
