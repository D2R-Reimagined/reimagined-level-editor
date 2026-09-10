using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>
    /// Lists the links this pair can no longer use and offers to discard them. Discarding
    /// removes only the editor's record: DS1 blocking, placements and HD models all keep
    /// their current values, exactly as Unlink does for an intact link.
    /// </summary>
    private void ReviewLinks_Click(object sender, RoutedEventArgs e)
    {
        if (placementLinks is not { Warning: null } links) { Status.Text = "Load the JSON and matching DS1 first."; return; }
        var broken = links.LinkStates.Where(l => !l.IsHealthy).ToArray();
        if (broken.Length == 0) { Status.Text = "Every link in this pair is intact."; RefreshLinkStatus(); return; }

        var dialog = new Window
        {
            Title = "Review broken links", Owner = this, Width = 720, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(32, 40, 51)), Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 244)),
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        dialog.Content = root;

        var heading = new TextBlock
        {
            Text = $"{broken.Length} of {links.Links.Count} links no longer match this pair.",
            FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

        var explanation = new TextBlock
        {
            Text = "Something these links describe changed outside this workspace. They are not moving anything, " +
                   "and the collision they used to own is editable again. Every other link keeps working.\n" +
                   "Discarding a link removes only the editor's record. No DS1 byte, placement or HD model changes, and one undo restores it.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(154, 175, 196)), Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(explanation, Dock.Top); root.Children.Add(explanation);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var list = new ListBox
        {
            SelectionMode = SelectionMode.Extended, Background = new SolidColorBrush(Color.FromRgb(24, 33, 43)),
            Foreground = new SolidColorBrush(Color.FromRgb(225, 232, 240)), BorderThickness = new Thickness(0),
        };
        root.Children.Add(list);

        void Populate()
        {
            var current = links.LinkStates.Where(l => !l.IsHealthy).ToArray();
            list.ItemsSource = current;
            heading.Text = current.Length == 0
                ? "Every link in this pair is intact."
                : $"{current.Length} of {links.Links.Count} links no longer match this pair.";
        }
        list.ItemTemplate = BrokenLinkTemplate();
        Populate();

        void Discard(IEnumerable<string>? ids, string label)
        {
            try
            {
                Scene.CancelDrag();
                // The recorded edit raises the shared history event, which refreshes both views.
                int count = links.DiscardBrokenLinks(ids);
                if (count == 0) { Status.Text = "Nothing to discard."; return; }
                Populate(); RefreshLinkStatus(); RefreshState();
                Status.Text = $"Discarded {count} broken link{(count == 1 ? "" : "s")} ({label}). Map data unchanged; Ctrl+Z restores the records.";
                Notify("Broken links discarded", $"{count} link record{(count == 1 ? "" : "s")} removed.\nNo DS1 or HD data changed. Ctrl+Z to undo.");
                if (!links.HasBrokenLinks) dialog.Close();
            }
            catch (Exception ex) { Error(ex); }
        }

        var discardSelected = new Button { Content = "Discard selected", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        discardSelected.Click += (_, _) => Discard(list.SelectedItems.OfType<LinkHealth>().Select(l => l.Link.EntityId).ToArray(), "selected");
        list.SelectionChanged += (_, _) => discardSelected.IsEnabled = list.SelectedItems.Count > 0;

        var discardAll = new Button { Content = "Discard all broken", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        discardAll.Click += (_, _) => Discard(null, "all broken");

        var close = new Button { Content = "Close", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        footer.Children.Add(discardSelected); footer.Children.Add(discardAll); footer.Children.Add(close);

        dialog.ShowDialog();
        RefreshLinkStatus(); RefreshState();
    }

    private static DataTemplate BrokenLinkTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Link.Name"));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var reason = new FrameworkElementFactory(typeof(TextBlock));
        reason.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Reason"));
        reason.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        reason.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(228, 186, 116)));

        var model = new FrameworkElementFactory(typeof(TextBlock));
        model.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Link.Model"));
        model.SetValue(TextBlock.FontSizeProperty, 11d);
        model.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(154, 175, 196)));
        model.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 4, 6));
        stack.AppendChild(name); stack.AppendChild(reason); stack.AppendChild(model);
        return new DataTemplate { VisualTree = stack };
    }
}
