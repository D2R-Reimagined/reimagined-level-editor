using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class SceneViewport
{
    public event Action<PresetEntity[], Vector3d, int>? DuplicationCommitted;
    private readonly ModelVisual3D duplicatePreview = new();
    private readonly TextBlock duplicateLabel = new()
    {
        Margin = new Thickness(12), Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(225, 19, 26, 35)),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private bool duplicating;
    private double duplicateSpacing;
    private int duplicateCount;
    private Vector3d duplicateStep;
    internal int DuplicatePreviewCount => duplicateCount * dragMembers.Count;
    internal double DuplicateSpacing => duplicateSpacing;
    internal Model3D? DuplicatePreviewGeometry => duplicatePreview.Content;

    private bool BeginDuplication(PresetEntity[] members, MovementAxis axis)
    {
        if (DuplicationCommitted is null || members.Length > PresetDocument.MaximumDuplicateModels ||
            members.Any(e => !GroupMovement.CanGroup(e) || placeholders.Contains(e.Index))) return false;
        var bounds = Rect3D.Empty;
        foreach (var member in members)
        {
            var visual = visuals[member.Index];
            bounds.Union(visual.Transform.TransformBounds(visual.Content.Bounds));
        }
        if (bounds.IsEmpty) return false;
        duplicateSpacing = axis switch { MovementAxis.X => bounds.SizeX, MovementAxis.Y => bounds.SizeY, _ => bounds.SizeZ };
        if (!double.IsFinite(duplicateSpacing) || duplicateSpacing < 0.0001) return false;
        duplicating = true; duplicateCount = 0; duplicateStep = default;
        duplicateLabel.Text = L.T("Alt-drag: repeat at model-sized intervals. Release to place; Esc cancels.");
        duplicateLabel.Visibility = Visibility.Visible;
        viewport.Children.Add(duplicatePreview);
        return true;
    }

    private void ContinueDuplication(Vector3D delta)
    {
        var direction = AxisVector(dragAxis!.Value);
        double travel = Vector3D.DotProduct(delta, direction);
        if (!double.IsFinite(travel)) return;
        int limit = PresetDocument.MaximumDuplicateModels / dragMembers.Count;
        double requested = Math.Floor(Math.Abs(travel) / duplicateSpacing + 1e-9);
        int count = (int)Math.Min(limit, requested);
        var vector = direction * (Math.Sign(travel) * duplicateSpacing);
        var step = new Vector3d(vector.X, vector.Y, vector.Z);
        duplicateLabel.Text = requested > limit
            ? L.T("{0} copies (drag limit). Release to place; Esc cancels.", count * dragMembers.Count)
            : L.T("{0} copies. Release to place; Esc cancels.", count * dragMembers.Count);
        if (count == duplicateCount && step == duplicateStep) return;
        duplicateCount = count; duplicateStep = step;
        var group = new Model3DGroup();
        for (int repeat = 1; repeat <= count; repeat++)
            foreach (var (member, start) in dragMembers)
            {
                var copy = new Model3DGroup { Transform = Transform(start with { Position = new(
                    start.Position.X + step.X * repeat, start.Position.Y + step.Y * repeat, start.Position.Z + step.Z * repeat) }) };
                copy.Children.Add(visuals[member.Index].Content);
                group.Children.Add(copy);
            }
        group.Freeze(); duplicatePreview.Content = group;
        UpdateGizmo();
    }

    private void ClearDuplication()
    {
        duplicating = false; duplicateCount = 0; duplicateStep = default;
        duplicatePreview.Content = null; viewport.Children.Remove(duplicatePreview);
        duplicateLabel.Visibility = Visibility.Collapsed;
    }
}
