using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using D2RLevel.Core;
using Reimagined.Integration;

namespace D2RLevel.App;

public partial class MainWindow
{
    private readonly DispatcherTimer companionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? companionRevision;
    private bool companionRefreshing;
    private void InitializeCompanion()
    {
        LevelDetails.StudioRequested += async (table, row, column) => await OpenStudioRecord(table, row, column);
        companionTimer.Tick += async (_, _) =>
        {
            if (companionRefreshing || StudioTableContext.Project?.Snapshot is not { } file || loading != null) return;
            companionRefreshing = true;
            try
            {
                var text = File.ReadAllText(file);
                if (text != companionRevision) { companionRevision = text; StudioTableContext.Invalidate(); await RefreshLevelProperties(); }
                var statusFile = Path.Combine(Path.GetDirectoryName(file)!, "status.json");
                if (File.Exists(statusFile))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(statusFile));
                    string error = json.RootElement.GetProperty("error").GetString() ?? "";
                    var time = json.RootElement.GetProperty("updated").GetDateTimeOffset();
                    CompanionStatus.Text = error.Length > 0 ? "Studio tables need attention: " + error : DateTimeOffset.UtcNow - time > TimeSpan.FromSeconds(15) ? "Studio table snapshot is offline · reopen Studio to refresh" : "Linked to Mod Studio · " + StudioTableContext.Project.Profile;
                }
                else CompanionStatus.Text = "Linked to Mod Studio · saved table snapshot";
            }
            catch (Exception ex) { CompanionStatus.Text = "Studio tables need attention: " + ex.Message; }
            finally { companionRefreshing = false; }
        };
        if (!arguments.Contains("--smoke-output")) companionTimer.Start();
        Closed += (_, _) => { companionTimer.Stop(); Program.Integration?.Dispose(); };
    }
    private Task<EditorReply> DispatchCompanion(EditorRequest request) => Dispatcher.InvokeAsync(() => ReceiveCompanion(request)).Task.Unwrap();
    private async Task<EditorReply> ReceiveCompanion(EditorRequest request)
    {
        try
        {
            request.Validate();
            if (request.Action == "open-table-record") return new("unsupported", "Table record navigation belongs to Mod Studio.");
            var idleDeadline = DateTime.UtcNow.AddSeconds(30);
            while (loading != null && DateTime.UtcNow < idleDeadline) await Task.Delay(100);
            if (request.Project == null && request.Target?.Preset is { } standalone)
            {
                if (loading != null) return new("failed", "Level Editor is loading. Retry when loading finishes.");
                if (!CanReplace()) return new("cancelled", "Scene navigation cancelled.");
                DisconnectStudio();
                if (request.Target.Assets is { } assets) resolver = new AssetResolver(assets);
                await LoadPreset(standalone);
            }
            if (request.Project is { } project)
            {
                if (loading != null) return new("failed", "Level Editor is loading. Retry when loading finishes.");
                if (request.Action == "refresh") { await RefreshLevelProperties(); return new("opened"); }
                if (!CanReplace()) return new("cancelled", "Scene navigation cancelled; unsaved edits were kept.");
                Program.Integration?.ClaimProject(project.Root);
                string root = IntegrationFiles.Inside(project.Root, "data");
                if (project.BaseData is { Length: > 0 }) resolver = new AssetResolver(project.BaseData);
                if (resolver == null) throw new InvalidOperationException("Choose the extracted game asset folder before opening a linked scene.");
                // Validate snapshot before replacing the previous context.
                var previous = StudioTableContext.Project; var oldMap = StudioTableContext.LogicalMap;
                StudioTableContext.Project = project; StudioTableContext.LogicalMap = request.Target?.LogicalMap;
                try
                {
                    _ = StudioTableContext.Resolve("levels");
                    if (request.Target is { Preset: not null, Map: not null } target && request.Action == "open-scene")
                    {
                        var json = IntegrationFiles.Inside(project.Root, target.Preset); var map = IntegrationFiles.Inside(project.Root, target.Map);
                        if (!File.Exists(json) || !File.Exists(map)) throw new FileNotFoundException("The linked scene pair is missing.");
                        var scene = new WorkspaceScene(Path.GetFileNameWithoutExtension(json), json, map);
                        openingWorkspaceSession = new WorkspaceSceneSession(project.Root, scene);
                        try { await LoadPreset(json); }
                        finally { openingWorkspaceSession = null; }
                        if (document == null || IntegrationFiles.Canonical(document.SourcePath) != IntegrationFiles.Canonical(json) || pairedScene?.Collision == null) throw new InvalidOperationException("The requested scene did not finish loading. See Level Editor diagnostics.");
                        workspaceFolder = root; workspaceScenes = Directory.Exists(root) ? SceneWorkspace.Scan(root) : [];
                    }
                    else { Directory.CreateDirectory(root); await LoadWorkspace(root); }
                    settings = settings with { StudioProjectRoot = project.Root, StudioProfile = project.Profile, StudioProjectId = project.Id, WorkspaceFolder = root };
                    SaveSettings();
                    CompanionStatus.Text = "Linked to Mod Studio · " + project.Profile;
                    ShowLevelProperties(true); RefreshState();
                    if (Argument("--integration-ui-smoke") is { } output && request.Action == "open-scene") await RecordCompanionSmoke(output);
                }
                catch { StudioTableContext.Project = previous; StudioTableContext.LogicalMap = oldMap; throw; }
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show(); Activate();
            return new("opened");
        }
        catch (Exception ex) { Error(ex); return new("failed", ex.Message); }
    }
    private void DisconnectStudio()
    {
        StudioTableContext.Project = null; StudioTableContext.LogicalMap = null; companionRevision = null;
        settings = settings with { StudioProjectRoot = null, StudioProfile = null, StudioProjectId = null };
        CompanionStatus.Text = ""; Program.Integration?.ReleaseProject(); SaveSettings();
    }
    private EditorProject? FindStudioProject()
    {
        if (StudioTableContext.Project is { } linked) return linked;
        var root = settings.StudioProjectRoot;
        if (root == null)
        {
            for (var dir = new DirectoryInfo(workspaceFolder ?? Path.GetDirectoryName(document?.SourcePath ?? "") ?? Environment.CurrentDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "mod-project.json"))) { root = dir.FullName; break; }
        }
        if (root == null || !Directory.Exists(root)) return null;
        string? id = settings.StudioProjectId;
        if (File.Exists(Path.Combine(root, "mod-project.json")))
        { using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "mod-project.json"))); id = json.RootElement.GetProperty("id").GetString(); }
        if (id == null) return null;
        string profile = settings.StudioProfile ?? "standard";
        var pointer = Path.Combine(root, ".studio/integration", profile, "current.json");
        return new(id, root, profile, resolver?.DataRoot, File.Exists(pointer) ? pointer : null);
    }
    private async Task OpenStudioRecord(string table, GameDataRow row, string column)
    {
        try
        {
            var project = FindStudioProject();
            if (project == null) { LinkStudioProject_Click(this, new()); project = FindStudioProject(); }
            if (project == null) return;
            string key = table == "lvlprest" ? "Def" : "Id";
            var target = new EditorTarget(Table: table, KeyColumn: key, KeyValue: row[key], SourceId: StudioTableContext.SourceId(table, row), Column: column);
            var reply = await CompanionApps.SendAsync(CompanionApps.Resolve(CompanionApps.Studio), new("open-table-record", project, target));
            Status.Text = reply.Success ? "Opened " + table + " · " + column + " in Mod Studio" : reply.Message;
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void OpenStudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var reply = await CompanionApps.SendAsync(CompanionApps.Resolve(CompanionApps.Studio), new("activate", FindStudioProject()));
            Status.Text = reply.Success ? "Opened Mod Studio" : reply.Message;
        }
        catch (Exception ex) { Error(ex); }
    }
    private void LinkStudioProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = L.T("Select the Mod Studio project root") };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (!File.Exists(Path.Combine(picker.FolderName, "mod-project.json"))) throw new InvalidDataException("Choose a folder containing mod-project.json.");
            settings = settings with { StudioProjectRoot = picker.FolderName, StudioProfile = "standard", StudioProjectId = null }; SaveSettings();
            CompanionStatus.Text = "Linked to Mod Studio project · " + picker.FolderName;
        }
        catch (Exception ex) { Error(ex); }
    }
    private void CompanionSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = L.T("Companion applications · Mod Studio"), Width = 720, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var stack = new StackPanel { Margin = new(20) };
        stack.Children.Add(new TextBlock { Text = "Mod Studio launch application (leave blank for automatic detection)" });
        var path = new TextBox { Text = CompanionApps.Override(CompanionApps.Studio) ?? "", Margin = new(0, 10, 0, 10) }; stack.Children.Add(path);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) }; stack.Children.Add(status);
        void Refresh() { try { var app = CompanionApps.Resolve(CompanionApps.Studio); status.Text = app.Source + " · " + app.Version + "\n" + app.Path; } catch (Exception ex) { status.Text = ex.Message; } }
        var buttons = new WrapPanel();
        var browse = new Button { Content = L.T("Browse…") }; browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Applications|*.exe", Title = L.T("Select Mod Studio application") }; if (picker.ShowDialog(dialog) == true) path.Text = picker.FileName; };
        var save = new Button { Content = L.T("Save") }; save.Click += (_, _) => { CompanionApps.SetOverride(CompanionApps.Studio, path.Text.Trim()); Refresh(); };
        var reset = new Button { Content = L.T("Reset to automatic") }; reset.Click += (_, _) => { path.Text = ""; CompanionApps.SetOverride(CompanionApps.Studio, null); Refresh(); };
        var test = new Button { Content = L.T("Test connection") }; test.Click += async (_, _) => { try { CompanionApps.SetOverride(CompanionApps.Studio, path.Text.Trim()); var app = await CompanionApps.ProbeAsync(CompanionApps.Resolve(CompanionApps.Studio)); status.Text = "Compatible Mod Studio · " + app.Version; } catch (Exception ex) { status.Text = ex.Message; } };
        var close = new Button { Content = L.T("Close") }; close.Click += (_, _) => dialog.Close();
        foreach (var b in new[] { browse, save, reset, test, close }) buttons.Children.Add(b);
        stack.Children.Add(buttons); dialog.Content = stack; Refresh(); dialog.ShowDialog();
    }
}
