using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace D2RLevel.App;

internal static class ControlTheme
{
    /// <summary>
    /// Makes a combo box's items legible. The application styles every TextBlock with a
    /// near-white foreground for its dark panels, and an implicit style is applied ahead
    /// of an inherited value, so setting Foreground on the combo box does not reach the
    /// item text. Combo box popups keep the system's light chrome, so the items need an
    /// explicit dark foreground of their own.
    /// </summary>
    /// <param name="displayMember">Property to show, or null to show the item itself.</param>
    public static ComboBox WithReadableItems(this ComboBox box, string? displayMember = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, displayMember is null ? new Binding() : new Binding(displayMember));
        text.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        box.ItemTemplate = new DataTemplate { VisualTree = text };
        box.Foreground = Brushes.Black;
        return box;
    }

    /// <summary>True when this combo box shows its items in a legible colour.</summary>
    public static bool HasReadableItems(this ComboBox box) =>
        box?.ItemTemplate?.VisualTree is { } tree && tree.Type == typeof(TextBlock);
}
