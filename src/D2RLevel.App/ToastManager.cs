using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace D2RLevel.App;

/// <summary>Window-local, nonmodal notifications; errors remain until dismissed.</summary>
internal sealed class ToastManager
{
    private static readonly ConditionalWeakTable<Window, ToastManager> Managers = new();
    private readonly StackPanel host = new() { Width = 380, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 72, 16, 16) };
    private readonly List<(Border Card, DateTime Expires)> entries = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ToastManager(Window window)
    {
        var content = (UIElement)window.Content;
        window.Content = null;
        var layout = new Grid(); layout.Children.Add(content); layout.Children.Add(host);
        Panel.SetZIndex(host, 1000); window.Content = layout;
        timer.Tick += (_, _) =>
        {
            foreach (var entry in entries.ToArray())
            {
                if (entry.Card.IsMouseOver || entry.Card.IsKeyboardFocusWithin)
                {
                    if (entry.Expires != DateTime.MaxValue)
                    { entries.Remove(entry); entries.Add((entry.Card, DateTime.UtcNow.AddSeconds(6))); }
                }
                else if (DateTime.UtcNow >= entry.Expires) Remove(entry.Card);
            }
        };
        window.Closed += (_, _) => { timer.Stop(); entries.Clear(); host.Children.Clear(); };
    }

    public static void Show(Window window, string title, string message, bool error = false)
    {
        if (!window.Dispatcher.CheckAccess())
        { window.Dispatcher.InvokeAsync(() => Show(window, title, message, error)); return; }
        Managers.GetValue(window, w => new(w)).Add(title, message, error);
    }

    private void Add(string title, string message, bool error)
    {
        while (entries.Count >= 3) Remove(entries[0].Card);
        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(32, 40, 51)),
            BorderBrush = new SolidColorBrush(error ? Color.FromRgb(244, 143, 143) : Color.FromRgb(139, 210, 180)),
            BorderThickness = new Thickness(3, 1, 1, 1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
        var layout = new DockPanel();
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top, ToolTip = "Dismiss notification" };
        System.Windows.Automation.AutomationProperties.SetName(close, "Dismiss notification");
        close.Click += (_, _) => Remove(card); DockPanel.SetDock(close, Dock.Right); layout.Children.Add(close);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        layout.Children.Add(text); card.Child = layout;
        System.Windows.Automation.AutomationProperties.SetName(card, title + ". " + message);
        host.Children.Add(card); entries.Add((card, error ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(6)));
        timer.Start();
    }

    private void Remove(Border card)
    {
        host.Children.Remove(card); entries.RemoveAll(e => e.Card == card);
        if (entries.Count == 0) timer.Stop();
    }
}
