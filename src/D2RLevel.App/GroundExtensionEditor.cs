using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Assets;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

/// <summary>Author a bounded candidate without changing the open scene or its saved files.</summary>
internal sealed class GroundExtensionEditor : Window
{
    private readonly TextBox[] inputs;
    private readonly GroundExtensionMap preview;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightGoldenrodYellow };
    private readonly Button export = new() { Content = L.T("Export ground extension…") };
    private readonly Button close = new() { Content = L.T("Close") };
    private readonly ComboBox pick = new ComboBox().WithReadableItems();
    private CancellationTokenSource? operation;
    private bool closeAfterCancel;
    public string? ExportedPreset { get; private set; }

    public GroundExtensionEditor(string preset, string map, AssetResolver assets, LevelTileset tileset, LegacyCollision collision,
        Action<GroundExtensionRequest?> showRegion)
    {
        Title = L.T("Extend ground (experimental)"); Width = 1000; Height = 740; MinWidth = 850; MinHeight = 680;
        Background = new SolidColorBrush(Color.FromRgb(19, 27, 36)); Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var heading = new TextBlock { Text = L.T("Extend an Act 1 cave floor. Select a rectangle and a walkable source tile. Optional growth adds up to 16 tiles per axis. HD ground and gameplay export together; enclosing walls still require authoring."), TextWrapping = TextWrapping.Wrap, Margin = new(6, 0, 6, 12) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        var actions = new WrapPanel(); actions.Children.Add(export); actions.Children.Add(close); footer.Children.Add(actions);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(285) }); grid.ColumnDefinitions.Add(new()); root.Children.Add(grid);
        var fields = new StackPanel { Margin = new(6) }; grid.Children.Add(new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        string[] labels = [L.T("First tile column"), L.T("First tile row"), L.T("End column (exclusive)"), L.T("End row (exclusive)"), L.T("Floor height"), L.T("Source floor column"), L.T("Source floor row"), L.T("Exported width (logical tiles)"), L.T("Exported height (logical tiles)"), L.T("Automap contour style (blank to omit)")];
        inputs = labels.Select(label => { fields.Children.Add(new TextBlock { Text = label }); var input = new TextBox { Text = "0", Margin = new(0, 2, 0, 6), ToolTip = label }; fields.Children.Add(input); return input; }).ToArray();
        inputs[7].Text=(collision.Document.Width-1).ToString(CultureInfo.InvariantCulture);
        inputs[8].Text=(collision.Document.Height-1).ToString(CultureInfo.InvariantCulture);
        inputs[9].Text="";
        fields.Children.Add(new TextBlock { Text = L.T("Click the map to choose:"), Margin = new(0, 8, 0, 2) });
        pick.ItemsSource = new[] { L.T("First corner"), L.T("Opposite corner (included)"), L.T("Source floor tile") }; pick.SelectedIndex = 0; fields.Children.Add(pick);
        fields.Children.Add(new TextBlock { Text = L.T("Gold: selected rectangle. Cyan: source floor. Gray: open floor. Dark: blocked or unresolved. Purple: protected map marker. Maximum 8 × 8 tiles without growth, 40 × 40 with growth."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) });
        preview = new(collision); Grid.SetColumn(preview, 1); grid.Children.Add(preview);
        foreach (var input in inputs) input.TextChanged += (_, _) => RefreshPreview();
        preview.Picked += (x, y) =>
        {
            if (operation is not null) return;
            int start = pick.SelectedIndex == 0 ? 0 : pick.SelectedIndex == 1 ? 2 : 5;
            int add = pick.SelectedIndex == 1 ? 1 : 0;
            inputs[start].Text = (x + add).ToString(CultureInfo.InvariantCulture);
            inputs[start + 1].Text = (y + add).ToString(CultureInfo.InvariantCulture);
            if (pick.SelectedIndex < 2) pick.SelectedIndex++;
        };
        GroundExtensionRequest Read()
        {
            int Int(int index) => int.Parse(inputs[index].Text, CultureInfo.InvariantCulture);
            int width=checked(Int(7)+1),height=checked(Int(8)+1);
            return new(Int(0), Int(1), Int(2), Int(3), float.Parse(inputs[4].Text, CultureInfo.InvariantCulture), Int(5), Int(6))
            { Growth=width==collision.Document.Width&&height==collision.Document.Height?null:new(width,height),ContourStyle=string.IsNullOrWhiteSpace(inputs[9].Text)?null:Int(9) };
        }
        void RefreshPreview()
        {
            GroundExtensionRequest? region = null;
            preview.GridWidth=collision.Document.Width;preview.GridHeight=collision.Document.Height;
            try
            {
                int width=checked(int.Parse(inputs[7].Text,CultureInfo.InvariantCulture)+1),height=checked(int.Parse(inputs[8].Text,CultureInfo.InvariantCulture)+1);
                if(width>=collision.Document.Width&&height>=collision.Document.Height&&width<=512&&height<=512&&width-collision.Document.Width<=16&&height-collision.Document.Height<=16)
                {preview.GridWidth=width;preview.GridHeight=height;}
            }
            catch(Exception ex) when(ex is FormatException or OverflowException) { }
            try
            {
                var r = Read();
                int width=r.Growth?.Width??collision.Document.Width,height=r.Growth?.Height??collision.Document.Height,span=r.Growth is null?8:40;
                if (width>=collision.Document.Width&&height>=collision.Document.Height&&width<=512&&height<=512&&width-collision.Document.Width<=16&&height-collision.Document.Height<=16&&float.IsFinite(r.Height) && r.MinX >= 0 && r.MinY >= 0 && r.MaxX > r.MinX && r.MaxY > r.MinY && r.MaxX < width && r.MaxY < height && r.MaxX - r.MinX <= span && r.MaxY - r.MinY <= span) region = r;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { }
            preview.Region = region; showRegion(region); export.IsEnabled = region is not null && operation is null;
            if (operation is null) status.Text = region is null ? L.T("Choose a rectangle inside the final border row and column.") : L.T("Preview only. Export validates terrain, scenery, collision and entrance markers before creating the candidate.");
        }
        export.Click += async (_, _) =>
        {
            if (operation is not null) return;
            var folder = new OpenFolderDialog { Title = L.T("Choose the parent folder for a new ground extension") };
            if (folder.ShowDialog(this) != true) return;
            var request = Read(); string destination = Path.Combine(folder.FolderName, Path.GetFileNameWithoutExtension(preset) + "-ground-" + Guid.NewGuid().ToString("N")[..8]);
            using var cts = new CancellationTokenSource(); operation = cts;
            fields.IsEnabled = false; export.IsEnabled = false; close.Content = L.T("Cancel export");
            status.Text = L.T("Preparing terrain, scenery and model catalog. Cancellation is available during preparation; a completed export may already be published.");
            try
            {
                ExportedPreset = await Task.Run(() => GroundExtensionExporter.Export(preset, map, assets, tileset, request, destination, cts.Token), cts.Token);
                operation = null; Close();
            }
            catch (OperationCanceledException) { status.Text = L.T("Export canceled. No candidate was published."); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                operation = null; fields.IsEnabled = true; export.IsEnabled = preview.Region is not null; close.Content = L.T("Close");
                if (closeAfterCancel) Close();
            }
        };
        close.Click += (_, _) => { if (operation is { } cts) { cts.Cancel(); status.Text = L.T("Canceling after the current model operation…"); } else Close(); };
        Closing += (_, e) => { if (operation is { } cts) { closeAfterCancel = true; cts.Cancel(); e.Cancel = true; status.Text = L.T("Canceling after the current model operation…"); } };
        RefreshPreview();
    }
}

internal sealed class GroundExtensionMap(LegacyCollision collision) : FrameworkElement
{
    public int GridWidth { get; set; } = collision.Document.Width;
    public int GridHeight { get; set; } = collision.Document.Height;
    private GroundExtensionRequest? region;
    public GroundExtensionRequest? Region { get => region; set { region = value; InvalidateVisual(); } }
    public event Action<int, int>? Picked;
    private (double Scale, double X, double Y) Layout()
    {
        int width=GridWidth,height=GridHeight;
        double s = Math.Max(.01, Math.Min((ActualWidth - 24) / width, (ActualHeight - 24) / height));
        return (s, (ActualWidth - s * width) / 2, (ActualHeight - s * height) / 2);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14, 21, 29)), null, new Rect(RenderSize));
        var (s, ox, oy) = Layout(); var map = collision.Document;
        for (int y = 0; y < GridHeight; y++) for (int x = 0; x < GridWidth; x++)
        {
            var cell = x<map.Width&&y<map.Height?collision.At(x, y):null;
            var brush = cell is null || cell.NoFloor || cell.Unresolved || cell.BlockedSubtiles == 25 ? Brushes.Black : cell.BlockedSubtiles > 0 ? Brushes.DimGray : Brushes.SlateGray;
            dc.DrawRectangle(brush, new Pen(Brushes.DarkSlateGray, .3), new(ox + x * s, oy + y * s, s, s));
        }
        foreach (var tile in map.ExitTiles()) dc.DrawRectangle(Brushes.MediumPurple, null, new(ox + tile.X * s, oy + tile.Y * s, s, s));
        if (Region is not { } r) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 255, 215, 0)), new Pen(Brushes.Gold, 2), new(ox + r.MinX * s, oy + r.MinY * s, (r.MaxX - r.MinX) * s, (r.MaxY - r.MinY) * s));
        if (r.DonorX >= 0 && r.DonorY >= 0 && r.DonorX < map.Width && r.DonorY < map.Height)
            dc.DrawRectangle(null, new Pen(Brushes.Cyan, 2), new(ox + r.DonorX * s, oy + r.DonorY * s, s, s));
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); if (e.ChangedButton != MouseButton.Left) return;
        var (s, ox, oy) = Layout(); var p = e.GetPosition(this);
        int x = (int)Math.Floor((p.X - ox) / s), y = (int)Math.Floor((p.Y - oy) / s);
        if (x >= 0 && y >= 0 && x < GridWidth-1 && y < GridHeight-1) Picked?.Invoke(x, y);
    }
}
