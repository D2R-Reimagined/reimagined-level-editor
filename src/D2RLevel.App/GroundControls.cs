using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private readonly ComboBox groundPalette = new() { Width = 245, MaxDropDownHeight = 350, Margin = new Thickness(4), Foreground = Brushes.Black };
    private readonly ComboBox groundLayer = new() { Width = 100, Margin = new Thickness(4), Foreground = Brushes.Black };
    private readonly ComboBox groundSize = new() { Width = 80, ItemsSource = new[] { 1, 3, 5, 9 }, SelectedIndex = 0, Margin = new Thickness(4), Foreground = Brushes.Black };
    private int strokeTool, strokeLayer, strokeSize = 1;
    private uint strokeFloor;
    private (int X, int Y)? rectangleStart;

    private void InitializeGround(DockPanel root)
    {
        var row = new WrapPanel(); DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
        row.Children.Add(new TextBlock { Text = "Ground", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) });
        groundLayer.WithReadableItems(); groundSize.WithReadableItems();
        foreach (var key in scene.Tiles.Keys.Where(k => k.Main is >= 0 and <= 63 && k.Sub is >= 0 and <= 255).OrderBy(k => k.Main).ThenBy(k => k.Sub))
        {
            var variants = scene.Tiles[key];
            var entry = new StackPanel { Orientation = Orientation.Horizontal };
            entry.Children.Add(new Image { Source = Bitmap(variants[0]), Width = 64, Height = 32 });
            entry.Children.Add(new TextBlock { Text = $"{key.Main}:{key.Sub} · {variants.Length} variant(s)", Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            groundPalette.Items.Add(new ComboBoxItem { Content = entry, Tag = Ds1CollisionDocument.FloorKey(key.Main, key.Sub), ToolTip = System.IO.Path.GetFileName(variants[0].Source) });
        }
        groundPalette.SelectedIndex = groundPalette.Items.Count > 0 ? 0 : -1;
        groundLayer.ItemsSource = Enumerable.Range(1, scene.Map.Layers.Length).Select(i => "Floor " + i).ToArray(); groundLayer.SelectedIndex = 0;
        row.Children.Add(groundPalette); row.Children.Add(groundLayer);
        row.Children.Add(new TextBlock { Text = "Brush size", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(groundSize);
        var hint = new TextBlock { Text = "Choose Paint floor, Erase floor, Pick floor or Fill floor above. Changes affect gameplay tiles; HD terrain keeps its existing mesh.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        groundPalette.SelectionChanged += (_, _) => CancelStroke(); groundLayer.SelectionChanged += (_, _) => CancelStroke(); groundSize.SelectionChanged += (_, _) => CancelStroke();
    }

    private bool BeginGround(Point point)
    {
        strokeTool = collisionTool.SelectedIndex;
        strokeLayer = groundLayer.SelectedIndex;
        strokeSize = groundSize.SelectedItem is int size ? size : 1;
        strokeFloor = groundPalette.SelectedItem is ComboBoxItem { Tag: uint tile } ? tile : 0;
        if (strokeTool < 3) return true;
        var at = TileAt(point);
        if (at is null) return false;
        if (strokeTool == 5)
        {
            var cell = scene.FloorAt(strokeLayer, at.Value.X, at.Value.Y);
            groundPalette.SelectedItem = groundPalette.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is uint raw && raw == Ds1CollisionDocument.FloorKey(cell.Main, cell.Sub));
            collisionStatus.Text = cell.IsEmpty ? "Empty floor." : $"Picked floor {cell.Main}:{cell.Sub}.";
            return false;
        }
        if (strokeTool != 4 && strokeFloor == 0) { collisionStatus.Text = "Choose a resolved ground tile first."; return false; }
        if (strokeTool == 6)
        {
            var cells = GroundBrush.Flood(CollisionDocument!, strokeLayer, at.Value.X, at.Value.Y);
            ApplyGround(cells);
            return false;
        }
        return true;
    }
    private int ApplyGround(IEnumerable<(int X, int Y)> cells)
    {
        uint tile = strokeTool == 4 ? 0 : strokeFloor;
        int count = collisionLinks is not null ? collisionLinks.PaintFloor(strokeLayer, cells, tile) : CollisionDocument!.PaintFloor(strokeLayer, cells, tile);
        CollisionChanged(); collisionStatus.Text = $"{count} floor cells changed · one undo restores this edit. HD terrain geometry is unchanged.";
        return count;
    }
}
