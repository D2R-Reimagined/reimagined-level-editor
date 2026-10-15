using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal enum ContactKind { Touching, Aligned }

/// <summary>
/// Two box faces that meet while a model is dragged. <see cref="Plane"/> is the shared
/// coordinate on <see cref="Axis"/>; <see cref="Face"/> spans the other two axes in order
/// (Y,Z for X; X,Z for Y; X,Y for Z). A null <see cref="Other"/> is the ground plane.
/// </summary>
internal sealed record ModelContact(PresetEntity? Other, MovementAxis Axis, ContactKind Kind, double Plane, Rect Face);

/// <summary>What the dragged selection stands on: the nearest top surface beneath its base.</summary>
internal sealed record ModelSupport(PresetEntity? Other, double Gap);

public sealed partial class SceneViewport
{
    // Faces closer than this are touching. Snapping lands exactly, so only float noise remains.
    internal const double ContactEpsilon = 1e-3;
    // Within this many pixels on screen a dragged face snaps to a neighbour's.
    private const double ContactSnapPixels = 8;
    private readonly ContactIndicator contactIndicator = new() { IsHitTestVisible = false };
    private readonly Dictionary<ModelVisual3D, (Transform3D Transform, Model3D Content, Rect3D Bounds)> exactBounds = new();
    private (PresetEntity Entity, Rect3D Bounds)[] contactMembers = [];
    private (PresetEntity Entity, ModelVisual3D Visual, Rect3D Coarse)[] contactNeighbors = [];
    internal IReadOnlyList<ModelContact> Contacts => contactIndicator.Contacts;
    internal ModelSupport? Support => contactIndicator.Support;
    internal int ContactFaceCount => contactIndicator.Faces.Count;
    internal string? ContactLabel => contactIndicator.Label;

    /// <summary>Captures the moving members and every visible model they could meet, once per drag.</summary>
    private void BeginContacts(IEnumerable<PresetEntity> members)
    {
        var moving = members.ToHashSet();
        contactMembers = moving.Where(e => !placeholders.Contains(e.Index) && visuals.ContainsKey(e.Index))
            .Select(e => (e, ExactBounds(visuals[e.Index]))).Where(m => !m.Item2.IsEmpty).ToArray();
        contactNeighbors = contactMembers.Length == 0 ? [] : visuals.Where(pair =>
        {
            var entity = entities[pair.Value];
            if (moving.Contains(entity) || placeholders.Contains(entity.Index) || (entity.IsTerrain && !terrainVisible)) return false;
            return entityBatches.TryGetValue(entity.Index, out var batches)
                ? batches.Any(b => batchVisuals[b].Content is not null) : viewport.Children.Contains(pair.Value);
        }).Select(pair => (entities[pair.Value], pair.Value, pair.Value.Transform.TransformBounds(pair.Value.Content.Bounds)))
            .Where(n => !n.Item3.IsEmpty).ToArray();
    }

    private void ClearContacts()
    {
        contactMembers = []; contactNeighbors = [];
        contactIndicator.Contacts.Clear(); contactIndicator.Faces.Clear();
        contactIndicator.Support = null; contactIndicator.Label = null; contactIndicator.Anchor = null;
        contactIndicator.InvalidateVisual();
    }

    /// <summary>
    /// Snaps the drag delta onto a nearby contact or alignment along the axes being moved, then
    /// records every face the moved selection now shares with another model or the ground.
    /// </summary>
    private Vector3D UpdateContacts(Vector3D delta, bool snap)
    {
        if (contactMembers.Length == 0) return delta;
        var axes = dragAxis is { } single ? [(int)single] : new[] { 0, 2 };
        double pixel = WorldPerPixel(Offset(Union(contactMembers.Select(m => m.Bounds)), delta));
        if (snap && pixel > 0)
            foreach (int axis in axes)
            {
                double reach = ContactSnapPixels * pixel, offset = reach * 2;
                void Consider(double candidate) { if (Math.Abs(candidate) < Math.Abs(offset)) offset = candidate; }
                foreach (var (_, start) in contactMembers)
                {
                    var b = Offset(start, delta);
                    if (axis == 1) Consider(-b.Y);
                    foreach (var neighbor in Neighbors(b, reach))
                    {
                        var n = neighbor.Bounds;
                        if (Overlaps(b, n, axis))
                        {
                            Consider(Max(n, axis) - Min(b, axis));
                            Consider(Min(n, axis) - Max(b, axis));
                        }
                        if (!neighbor.Entity.IsTerrain && Near(b, n, axis))
                        {
                            Consider(Min(n, axis) - Min(b, axis));
                            Consider(Max(n, axis) - Max(b, axis));
                        }
                    }
                }
                if (Math.Abs(offset) <= reach) delta += AxisVector((MovementAxis)axis) * offset;
            }
        MeasureContacts(delta, axes);
        return delta;
    }

    private void MeasureContacts(Vector3D delta, int[] movedAxes)
    {
        var contacts = contactIndicator.Contacts; contacts.Clear();
        var moved = contactMembers.Select(m => (m.Entity, Bounds: Offset(m.Bounds, delta))).ToArray();
        foreach (var (_, b) in moved)
        {
            if (Math.Abs(b.Y) <= ContactEpsilon)
                contacts.Add(new(null, MovementAxis.Y, ContactKind.Touching, 0, Face(b, 1)));
            foreach (var neighbor in Neighbors(b, ContactEpsilon))
            {
                var n = neighbor.Bounds;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (Overlaps(b, n, axis))
                        foreach (var (mine, theirs) in new[] { (Min(b, axis), Max(n, axis)), (Max(b, axis), Min(n, axis)) })
                            if (Math.Abs(mine - theirs) <= ContactEpsilon)
                                contacts.Add(new(neighbor.Entity, (MovementAxis)axis, ContactKind.Touching, theirs, Intersect(Face(b, axis), Face(n, axis))));
                    if (movedAxes.Contains(axis) && !neighbor.Entity.IsTerrain && Near(b, n, axis))
                        foreach (var (mine, theirs) in new[] { (Min(b, axis), Min(n, axis)), (Max(b, axis), Max(n, axis)) })
                            if (Math.Abs(mine - theirs) <= ContactEpsilon && (axis != 1 || !Overlaps(b, n, axis)))
                                contacts.Add(new(neighbor.Entity, (MovementAxis)axis, ContactKind.Aligned, theirs, Union(Face(b, axis), Face(n, axis))));
                }
            }
        }
        // Many floor tiles can share one base; keep the drawing legible.
        if (contacts.Count > 48)
        {
            var kept = contacts.OrderBy(c => c.Kind).Take(48).ToArray();
            contacts.Clear(); contacts.AddRange(kept);
        }
        var selection = Union(moved.Select(m => m.Bounds));
        contactIndicator.Support = FindSupport(selection);
        ProjectContacts(selection);
    }

    /// <summary>The highest top surface under the selection's base, allowing for one sunk partway in.</summary>
    private ModelSupport? FindSupport(Rect3D b)
    {
        ModelSupport? best = b.Y >= -b.SizeY / 2 ? new(null, b.Y) : null;
        foreach (var neighbor in Neighbors(b, double.PositiveInfinity, vertical: true))
        {
            var n = neighbor.Bounds;
            double top = n.Y + n.SizeY;
            if (!Overlaps(b, n, 1) || top > b.Y + b.SizeY / 2) continue;
            double gap = b.Y - top;
            if (best is null || Math.Abs(gap) < Math.Abs(best.Gap) || (best.Other is null && gap <= best.Gap)) best = new(neighbor.Entity, gap);
        }
        return best;
    }

    /// <summary>Neighbours whose box lies within <paramref name="reach"/> of <paramref name="b"/>, with exact bounds.</summary>
    private IEnumerable<(PresetEntity Entity, Rect3D Bounds)> Neighbors(Rect3D b, double reach, bool vertical = false)
    {
        double span = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
        foreach (var (entity, visual, coarse) in contactNeighbors)
        {
            // Alignment looks one model-width sideways; a support search looks straight down.
            double side = vertical ? 0 : span + reach;
            if (Gap(b.X, b.SizeX, coarse.X, coarse.SizeX) > side || Gap(b.Z, b.SizeZ, coarse.Z, coarse.SizeZ) > side) continue;
            if (!vertical && Gap(b.Y, b.SizeY, coarse.Y, coarse.SizeY) > side) continue;
            var exact = ExactBounds(visual);
            if (!exact.IsEmpty) yield return (entity, exact);
        }
    }

    private void ProjectContacts(Rect3D selection)
    {
        var faces = contactIndicator.Faces; faces.Clear();
        foreach (var contact in contactIndicator.Contacts)
        {
            if (contact.Face.IsEmpty) continue;
            var corners = new Point[4]; bool visible = true;
            for (int i = 0; i < 4; i++)
            {
                double u = i is 0 or 3 ? contact.Face.Left : contact.Face.Right, v = i < 2 ? contact.Face.Top : contact.Face.Bottom;
                var world = contact.Axis switch
                {
                    MovementAxis.X => new Point3D(contact.Plane, u, v),
                    MovementAxis.Y => new Point3D(u, contact.Plane, v),
                    _ => new Point3D(u, v, contact.Plane)
                };
                if (Project(world) is { } point) corners[i] = point; else { visible = false; break; }
            }
            if (visible) faces.Add((contact.Kind, corners));
        }
        contactIndicator.Label = null;
        if (contactIndicator.Support is { } support && !selection.IsEmpty)
        {
            var b = selection;
            string name = support.Other is { } other ? ContactName(other) : L.T("ground");
            string gap = Math.Abs(support.Gap).ToString("0.###", CultureInfo.CurrentCulture);
            contactIndicator.Touching = Math.Abs(support.Gap) <= ContactEpsilon;
            contactIndicator.Label = (contactIndicator.Touching ? L.T("Resting on {0}", name)
                : support.Gap > 0 ? L.T("{0} above {1}", gap, name) : L.T("{0} into {1}", gap, name)) +
                "\n" + L.T("Hold Ctrl to move without snapping");
            contactIndicator.Anchor = Project(new(b.X + b.SizeX / 2, b.Y, b.Z + b.SizeZ / 2));
        }
        contactIndicator.InvalidateVisual();
    }

    private static string ContactName(PresetEntity entity) =>
        entity.SourceAsset is { } asset ? System.IO.Path.GetFileNameWithoutExtension(asset) : entity.Name;

    private double WorldPerPixel(Rect3D bounds)
    {
        if (bounds.IsEmpty || ActualWidth <= 0) return 0;
        var forward = camera.LookDirection; forward.Normalize();
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double depth = Vector3D.DotProduct(center - camera.Position, forward);
        double size = 2 * Math.Tan(camera.FieldOfView * Math.PI / 360) * depth / ActualWidth;
        return double.IsFinite(size) && size > 0 ? size : 0;
    }

    /// <summary>
    /// World bounds from the transformed vertices, cached until the transform or content changes.
    /// Transforming a local box instead would swell rotated models and misreport their contacts.
    /// </summary>
    private Rect3D ExactBounds(ModelVisual3D visual)
    {
        if (visual.Content is not { } content) return Rect3D.Empty;
        var transform = visual.Transform ?? Transform3D.Identity;
        if (exactBounds.TryGetValue(visual, out var cached) && ReferenceEquals(cached.Transform, transform) && ReferenceEquals(cached.Content, content))
            return cached.Bounds;
        var box = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Accumulate(content, transform.Value, box);
        var bounds = box[0] <= box[3] ? new Rect3D(box[0], box[1], box[2], box[3] - box[0], box[4] - box[1], box[5] - box[2]) : Rect3D.Empty;
        exactBounds[visual] = (transform, content, bounds);
        return bounds;
    }

    private static void Accumulate(Model3D model, Matrix3D parent, double[] box)
    {
        var matrix = model.Transform is { } t ? t.Value * parent : parent;
        if (model is Model3DGroup group) { foreach (var child in group.Children) Accumulate(child, matrix, box); return; }
        if (model is not GeometryModel3D { Geometry: MeshGeometry3D { Positions: { } positions } }) return;
        foreach (var local in positions)
        {
            var p = matrix.Transform(local);
            box[0] = Math.Min(box[0], p.X); box[1] = Math.Min(box[1], p.Y); box[2] = Math.Min(box[2], p.Z);
            box[3] = Math.Max(box[3], p.X); box[4] = Math.Max(box[4], p.Y); box[5] = Math.Max(box[5], p.Z);
        }
    }

    private static double Min(Rect3D b, int axis) => axis switch { 0 => b.X, 1 => b.Y, _ => b.Z };
    private static double Max(Rect3D b, int axis) => axis switch { 0 => b.X + b.SizeX, 1 => b.Y + b.SizeY, _ => b.Z + b.SizeZ };
    private static (int U, int V) Across(int axis) => axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
    private static Rect Face(Rect3D b, int axis)
    {
        var (u, v) = Across(axis);
        return new(Min(b, u), Min(b, v), Max(b, u) - Min(b, u), Max(b, v) - Min(b, v));
    }
    private static Rect Intersect(Rect a, Rect b) { a.Intersect(b); return a; }
    private static Rect Union(Rect a, Rect b) { a.Union(b); return a; }
    private static Rect3D Offset(Rect3D b, Vector3D delta) => b.IsEmpty ? b : new(b.X + delta.X, b.Y + delta.Y, b.Z + delta.Z, b.SizeX, b.SizeY, b.SizeZ);
    private static Rect3D Union(IEnumerable<Rect3D> boxes)
    {
        var result = Rect3D.Empty;
        foreach (var box in boxes) result.Union(box);
        return result;
    }
    private static double Gap(double a, double aSize, double b, double bSize) => Math.Max(b - (a + aSize), a - (b + bSize));
    /// <summary>The boxes share area across <paramref name="axis"/>, so faces on it can press together.</summary>
    private static bool Overlaps(Rect3D a, Rect3D b, int axis)
    {
        var (u, v) = Across(axis);
        return Math.Min(Max(a, u), Max(b, u)) - Math.Max(Min(a, u), Min(b, u)) > ContactEpsilon &&
            Math.Min(Max(a, v), Max(b, v)) - Math.Max(Min(a, v), Min(b, v)) > ContactEpsilon;
    }
    /// <summary>The boxes sit within one model-width of each other across <paramref name="axis"/>.</summary>
    private static bool Near(Rect3D a, Rect3D b, int axis)
    {
        var (u, v) = Across(axis);
        double reach = Math.Max(a.SizeX, Math.Max(a.SizeY, a.SizeZ));
        return Gap(Min(a, u), Max(a, u) - Min(a, u), Min(b, u), Max(b, u) - Min(b, u)) <= reach &&
            Gap(Min(a, v), Max(a, v) - Min(a, v), Min(b, v), Max(b, v) - Min(b, v)) <= reach;
    }
}

// Drawn as a 2D overlay like the gizmo so contact faces stay readable inside or behind models.
internal sealed class ContactIndicator : FrameworkElement
{
    internal List<ModelContact> Contacts { get; } = [];
    internal List<(ContactKind Kind, Point[] Corners)> Faces { get; } = [];
    internal ModelSupport? Support { get; set; }
    internal string? Label { get; set; }
    internal Point? Anchor { get; set; }
    internal bool Touching { get; set; }

    private static readonly Brush TouchFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 80, 220, 120)));
    private static readonly Pen TouchPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(90, 240, 130)), 2));
    private static readonly Pen AlignPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 196, 70)), 1.5) { DashStyle = DashStyles.Dash });
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (kind, corners) in Faces.OrderBy(f => f.Kind == ContactKind.Touching))
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(corners[0], true, true);
                context.PolyLineTo(corners[1..], true, false);
            }
            geometry.Freeze();
            if (kind == ContactKind.Touching) dc.DrawGeometry(TouchFill, TouchPen, geometry);
            else dc.DrawGeometry(null, AlignPen, geometry);
        }
        if (Label is null || Anchor is not { } anchor) return;
        var text = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 12, Touching ? TouchPen.Brush : Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(Math.Clamp(anchor.X - text.Width / 2, 4, Math.Max(4, ActualWidth - text.Width - 4)),
            Math.Clamp(anchor.Y + 14, 4, Math.Max(4, ActualHeight - text.Height - 4)));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 19, 26, 35)), Touching ? TouchPen : null,
            new Rect(origin - new Vector(6, 3), new Size(text.Width + 12, text.Height + 6)), 4, 4);
        dc.DrawText(text, origin);
    }
}
