using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private LevelPlacement[] placementChoices = [];
    public void ConfigurePlacements(LevelPlacement[]? templates)
    {
        placementChoices = (templates ?? []).Concat(CollisionDocument?.Units.Where(u => u.Type is 1 or 2)
            .Select(u => new LevelPlacement((u.Type == 1 ? L.T("NPC/monster") : L.T("Object")) + " " + u.Id, u.Type, u.Id, u.Flags)) ?? [])
            .DistinctBy(p => (p.Type, p.Id, p.Flags)).ToArray();
    }
    private void AddPlacement()
    {
        CancelStroke();
        if (collisionLinks is null) { unitStatus.Text = L.T("Open a paired workspace to add gameplay placements."); return; }
        ConfigurePlacements(placementChoices);
        if (placementChoices.Length == 0) { unitStatus.Text = L.T("No template placements are available. Create a level from a scene with supported gameplay units."); return; }
        var panel = new StackPanel { Margin = new Thickness(18) };
        var choices = new ComboBox { ItemsSource = placementChoices.Select(p => L.T("{0} · flags 0x{1:X}", p.Name, p.Flags)).ToArray(), SelectedIndex = 0, Foreground = Brushes.Black, Margin = new Thickness(3) }; choices.WithReadableItems();
        var x = new TextBox { Text = string.IsNullOrWhiteSpace(unitX.Text) ? "2" : unitX.Text };
        var y = new TextBox { Text = string.IsNullOrWhiteSpace(unitY.Text) ? "2" : unitY.Text };
        panel.Children.Add(new TextBlock { Text = L.T("Place a gameplay unit"), FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(choices);
        panel.Children.Add(new TextBlock { Text = L.T("Subtile X") }); panel.Children.Add(x);
        panel.Children.Add(new TextBlock { Text = L.T("Subtile Y") }); panel.Children.Add(y);
        panel.Children.Add(new TextBlock { Text = L.T("These IDs and flags come from this environment's template. Special NPC services and quest behavior need in-game checks. Adding a unit does not create decorative HD geometry or an owned collision footprint."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; var add = new Button { Content = L.T("Place"), IsDefault = true }; buttons.Children.Add(cancel); buttons.Children.Add(add);
        var dialog = new Window { Owner = this, Title = L.T("Place gameplay unit"), Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Content = panel };
        add.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(x.Text, out int px) || !int.TryParse(y.Text, out int py)) throw new InvalidOperationException("Enter whole subtile coordinates.");
                var choice = placementChoices[choices.SelectedIndex];
                int index = collisionLinks.AppendUnit(choice.Type, choice.Id, px, py, choice.Flags);
                CollisionChanged(); SelectGameplayUnit(index); dialog.DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        dialog.ShowDialog();
    }
    private void DeletePlacement()
    {
        CancelStroke();
        if (collisionLinks is null || unitList.SelectedIndex < 0) return;
        try { collisionLinks.DeleteUnit(unitList.SelectedIndex); CollisionChanged(); }
        catch (Exception ex) { unitStatus.Text = ex.Message; }
    }
}
