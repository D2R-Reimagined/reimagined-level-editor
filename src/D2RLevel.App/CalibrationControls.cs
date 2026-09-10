using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>
    /// Restores this pair's stored registration. A pair that has never been calibrated
    /// keeps the historical default and is only *offered* a terrain measurement: the
    /// terrain mesh is not guaranteed to span exactly the DS1 grid, so accepting that
    /// estimate is the user's call, not a silent change to every placement conversion.
    /// </summary>
    private void InitializeCalibration()
    {
        if (placementLinks is { Warning: null } links && !links.Calibration.IsUnverified)
        { Ds1Preview.SetCalibration(links.Calibration); return; }
        Ds1Preview.SetCalibration(GridCalibration.Unverified);
        if (pairedScene is null) return;
        if (MeasureTerrainCalibration() is not { } measured) return;
        Status.Text = $"Grid calibration is still the unverified default. Terrain in this scene measures {measured.Describe()} " +
            "Open Calibrate… to review and apply it.";
    }

    /// <summary>Measures the terrain mesh extent against the paired DS1's tile grid.</summary>
    private GridCalibration? MeasureTerrainCalibration()
    {
        if (pairedScene is null) return null;
        var bounds = Scene.TerrainBounds();
        if (bounds.IsEmpty) return null;
        try { return GridCalibration.FromTerrain(bounds.SizeX, bounds.SizeZ, pairedScene.Map.Width, pairedScene.Map.Height); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return null; }
    }

    private void ApplyCalibration(GridCalibration value, bool announce = true)
    {
        Ds1Preview.SetCalibration(value);
        // Persisting needs a sidecar; a pair without one keeps the calibration for this session.
        if (placementLinks is { Warning: null } links)
        {
            try { links.SetCalibration(value); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            { Status.Text = "Calibration applied to this session only: " + ex.Message; RefreshState(); return; }
        }
        RefreshLinkStatus(); RefreshState();
        if (announce)
            Status.Text = "Grid calibration: " + value.Describe() +
                (placementLinks is { Warning: null } ? " Save Scene or Save linked pair to keep it with this pair." : "");
    }

    private void Calibrate_Click()
    {
        if (pairedScene is null) { Status.Text = "Open the paired DS1 before calibrating."; return; }
        var dialog = new Window
        {
            Title = "Grid calibration", Owner = this, Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(32, 40, 51)), Foreground = new SolidColorBrush(Color.FromRgb(230, 237, 244)),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var current = Ds1Preview.Calibration;

        void Line(string text, Brush? brush = null, bool bold = false) => panel.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Foreground = brush ?? dialog.Foreground, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        });

        Line("How many HD units span one DS1 tile in this scene?", bold: true);
        Line("Unit links, footprint suggestions and NPC placement all divide by this number. " +
             "Objects already linked keep the scale they were created with, so changing it here cannot move them.",
             new SolidColorBrush(Color.FromRgb(154, 175, 196)));
        Line("Current: " + current.Describe());
        if (current.Warning is { } warning) Line(warning, new SolidColorBrush(Color.FromRgb(228, 186, 116)));

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        GridCalibration? chosen = null;
        void Offer(GridCalibration value)
        {
            chosen = value;
            result.Foreground = value.Warning is null ? new SolidColorBrush(Color.FromRgb(139, 210, 212)) : new SolidColorBrush(Color.FromRgb(228, 186, 116));
            result.Text = "Proposed: " + value.Describe() + (value.Warning is null ? "" : "\n" + value.Warning);
        }
        void Fail(string message)
        {
            chosen = null;
            result.Foreground = new SolidColorBrush(Color.FromRgb(228, 186, 116)); result.Text = message;
        }

        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        void Action(string label, string tip, Func<GridCalibration> solve)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 8), ToolTip = tip };
            button.Click += (_, _) =>
            {
                try { Offer(solve()); }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Fail(ex.Message); }
            };
            buttons.Children.Add(button);
        }
        Action("Measure from terrain", "Compare the terrain mesh extent with the DS1 tile grid.",
            () => MeasureTerrainCalibration() ?? throw new InvalidOperationException(
                "This scene has no decoded terrain to measure. Load the asset folder and terrain, or use another method."));
        Action("Solve from existing links", "Fit the scale to every object already linked to a DS1 placement.",
            () => (placementLinks ?? throw new InvalidOperationException("Load the JSON and matching DS1 first.")).SolveCalibrationFromLinks());

        var manual = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(manual);
        manual.Children.Add(new TextBlock { Text = "Or enter it directly:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var typed = new TextBox { Width = 80, Text = current.UnitsPerTile.ToString("0.####", CultureInfo.InvariantCulture) };
        manual.Children.Add(typed);
        var useTyped = new Button { Content = "Use this value", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(8, 0, 0, 0) };
        useTyped.Click += (_, _) =>
        {
            try
            {
                if (!double.TryParse(typed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new InvalidDataException("Enter a number, for example 10.");
                Offer(GridCalibration.Typed(value));
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Fail(ex.Message); }
        };
        manual.Children.Add(useTyped);
        panel.Children.Add(result);

        // Lead with the measurement this scene can already make, without committing to it.
        if (current.IsUnverified && MeasureTerrainCalibration() is { } suggestion) Offer(suggestion);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(footer);
        var apply = new Button { Content = "Apply", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(40, 104, 108)) };
        apply.Click += (_, _) =>
        {
            if (chosen is null) { Fail("Choose or enter a value first."); return; }
            dialog.DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        footer.Children.Add(apply); footer.Children.Add(cancel);

        if (dialog.ShowDialog() == true && chosen is { } value) ApplyCalibration(value);
    }
}
