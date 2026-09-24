using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>Read-only level context. Switching difficulty never changes game data.</summary>
public sealed class LevelPropertiesView : DockPanel
{
    private readonly ComboBox contexts = new ComboBox { Margin = new(0, 4, 0, 10) }.WithReadableItems(nameof(LevelPresetContext.DisplayName));
    private readonly ComboBox difficulty = new ComboBox { Margin = new(0, 4, 0, 10) }.WithReadableItems();
    private readonly StackPanel details = new();
    private readonly List<string> renderedText = [];
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGoldenrodYellow, Margin = new(0, 4, 0, 8) };
    private LevelProperties? snapshot;
    public event Action? RefreshRequested;
    public event Action<string, GameDataRow, string>? StudioRequested;
    internal int ContextCount => contexts.Items.Count;
    internal string DetailsText => string.Join("\n", renderedText);
    internal void SelectDifficulty(int index) => difficulty.SelectedIndex = index;
    internal void SelectContext(int index) => contexts.SelectedIndex = index;

    public LevelPropertiesView()
    {
        var header = new DockPanel { Margin = new(0, 6, 0, 4) };
        var refresh = new Button { Content = L.T("Refresh"), ToolTip = L.T("Read saved game tables again. Unsaved edits in other applications are not included.") };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(); SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        header.Children.Add(new TextBlock { Text = L.T("LEVEL PROPERTIES"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        SetDock(header, Dock.Top); Children.Add(header);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = L.T("Saved game data · read only"), Foreground = Brushes.LightSteelBlue, Margin = new(0, 0, 0, 6) });
        content.Children.Add(notice);
        content.Children.Add(new TextBlock { Text = L.T("Preset context") }); content.Children.Add(contexts);
        content.Children.Add(new TextBlock { Text = L.T("Difficulty") }); content.Children.Add(difficulty);
        difficulty.ItemsSource = new[] { L.T("Normal"), L.T("Nightmare"), L.T("Hell") }; difficulty.SelectedIndex = 0;
        content.Children.Add(details);
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        contexts.SelectionChanged += (_, _) => Render(); difficulty.SelectionChanged += (_, _) => Render();
        SetSnapshot(null);
    }

    public void SetSnapshot(LevelProperties? value)
    {
        snapshot = value;
        contexts.ItemsSource = value?.Contexts;
        contexts.SelectedIndex = value?.Contexts.Count == 1 ? 0 : -1;
        contexts.IsEnabled = value?.Contexts.Count > 1;
        Render();
    }

    private void Render()
    {
        details.Children.Clear(); renderedText.Clear();
        notice.Text = snapshot is null ? L.T("Open a paired scene to inspect its level data.") : snapshot.Warning ?? "";
        if (snapshot?.ProjectName is { } project)
            notice.Text += (notice.Text.Length > 0 ? "\n\n" : "") + L.T("Project: {0}. Area values below describe the retained filename's table context; this project does not register a new area.", project);
        notice.Visibility = notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        difficulty.IsEnabled = contexts.SelectedItem is LevelPresetContext { Level: not null };
        if (snapshot is null) return;
        if (snapshot.MapPath is { } map) Add(L.T("DS1"), PresetPairing.Split(map, "global/tiles")?.Relative ?? map, map);
        if (contexts.SelectedItem is LevelPresetContext context)
        {
            if (context.Warning is not null) Add(L.T("Context notice"), context.Warning, fullWidth: true);
            var preset = context.Preset;
            var editPreset = new Button { Content = L.T("Edit preset in Mod Studio") };
            editPreset.Click += (_, _) => StudioRequested?.Invoke("lvlprest", preset, "Def"); details.Children.Add(editPreset);
            if (context.Level is { } level)
            {
                var editLevel = new Button { Content = L.T("Edit level in Mod Studio") };
                editLevel.Click += (_, _) => StudioRequested?.Invoke("levels", level, "Id"); details.Children.Add(editLevel);
                var connections = new WrapPanel();
                for (int slot = 0; slot < 8; slot++)
                {
                    foreach (var prefix in new[] { "Vis", "Warp" })
                    {
                        string column = prefix + slot;
                        var edit = new Button { Content = column, ToolTip = "Edit " + column + " in Mod Studio" };
                        edit.Click += (_, _) => StudioRequested?.Invoke("levels", level, column); connections.Children.Add(edit);
                    }
                }
                details.Children.Add(connections);
                Heading(L.T("Area"));
                Field(level, "Name", L.T("Area record"));
                Field(level, "*StringName", L.T("Display name comment"));
                Field(level, "Id", L.T("Area ID"));
                Add(L.T("Act"), int.TryParse(level["Act"], out int act) ? (act + 1).ToString() : L.T("Not specified"), Source(level, "Act"));
                Field(level, "LevelName", L.T("Name string key"));
                Field(level, "DrlgType", L.T("Generator type"));
                Field(level, "LevelType", L.T("Tileset ID"));
                Heading(L.T("Difficulty settings"));
                int diff = Math.Max(0, difficulty.SelectedIndex);
                void DifficultyField(string column, string label) => Field(level, LevelProperties.DifficultyColumn(column, diff), label);
                DifficultyField("MonLvlEx", L.T("Area level · Expansion"));
                DifficultyField("MonLvl", L.T("Area level · Classic"));
                DifficultyField("MonDen", L.T("Monster density · raw"));
                DifficultyField("MonUMin", L.T("Unique monsters · minimum"));
                DifficultyField("MonUMax", L.T("Unique monsters · maximum"));
                DifficultyField("SizeX", L.T("Area width · table"));
                DifficultyField("SizeY", L.T("Area height · table"));
                Heading(L.T("Monster pool"));
                Field(level, "NumMon", L.T("Monster variety count"));
                var pool = LevelProperties.MonsterPool(level, diff);
                if (pool.Count == 0) Add(L.T("Random monsters"), L.T("No entries in this difficulty's pool."));
                foreach (var entry in pool) Add(entry.Column, entry.Value, Source(level, entry.Column));
                if (diff == 0)
                {
                    var unique = Enumerable.Range(1, 25).Select(i => "umon" + i).Where(c => level[c].Length > 0 && level[c] != "0").ToArray();
                    if (unique.Length > 0) Heading(L.T("Unique monster pool · Normal"));
                    foreach (string column in unique) Field(level, column, column);
                }
                Add(L.T("Pool context"), L.T("These are configured monster IDs, not a generated encounter. Density is a raw table value, not a monster count."), fullWidth: true);
            }
            Heading(L.T("Preset"));
            Field(preset, "Name", L.T("Preset name")); Field(preset, "Def", L.T("Preset ID"));
            Field(preset, "LevelId", L.T("Assigned area ID")); Field(preset, "Populate", L.T("Populate"));
            Field(preset, "Dt1Mask", L.T("DT1 mask"));
            Heading(L.T("Preset variants"));
            foreach (int i in Enumerable.Range(1, 6).Where(i => preset[$"File{i}"] is { Length: > 0 } v && v != "0"))
                Field(preset, $"File{i}", $"File{i}");
            if (context.LevelType is { } type)
            {
                Heading(L.T("Tileset definition")); Field(type, "Name", L.T("Tileset name"));
                Add(L.T("Tileset context"), L.T("Configured LvlTypes files follow. The preset DT1 mask selects from these; an authored project can override its loaded tileset."), fullWidth: true);
                for (int i = 1; i <= 32; i++) if (type[$"File {i}"] is { Length: > 0 } file && file != "0") Field(type, $"File {i}", $"File {i}");
            }
        }
        Heading(L.T("Data sources"));
        foreach (var table in snapshot.Sources)
        {
            Add(table.Name + ".txt · " + (table.IsOverride ? L.T("Workspace override") : L.T("Asset folder")),
                table.SourcePath ?? L.T("No source folder"), fullWidth: true);
            if (table.Warning is not null) Add(L.T("Unavailable"), table.Warning, fullWidth: true);
        }
    }

    private static string Source(GameDataRow row, string column) => $"{row.SourcePath}\nLine {row.Line} · {column}";
    private void Field(GameDataRow row, string column, string label) => Add(label,
        row[column].Length == 0 ? L.T("Not specified") : row[column], Source(row, column));
    private void Heading(string title)
    {
        renderedText.Add(title);
        details.Children.Add(new TextBlock
        { Text = title, FontWeight = FontWeights.Bold, Foreground = Brushes.LightSteelBlue, Margin = new(0, 14, 0, 5) });
    }
    private void Add(string label, string value, string? source = null, bool fullWidth = false)
    {
        renderedText.Add(label); renderedText.Add(value);
        if (fullWidth)
        {
            details.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.LightSteelBlue, Margin = new(0, 6, 0, 3), TextWrapping = TextWrapping.Wrap });
            details.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, ToolTip = source });
            return;
        }
        var row = new Grid { Margin = new(0, 4, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = new(0.46, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(0.54, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.LightSteelBlue, Margin = new(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, ToolTip = source };
        Grid.SetColumn(text, 1); row.Children.Add(text); details.Children.Add(row);
    }
}
