using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace D2RLevel.App;

public partial class MainWindow
{
    private UpdateManager? updater;
    private UpdateInfo? pendingUpdate;
    private bool updateBusy, updateDownloaded;

    private static string UpdateRepository => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "UpdateRepositoryUrl").Value!;

    private static string BuildVersion => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "0.0.0";

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy) return;
        if (pendingUpdate is null) { await CheckForUpdatesAsync(true); return; }
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (!updateDownloaded)
            {
                UpdateButton.Content = L.T("Downloading update…");
                await updater!.DownloadUpdatesAsync(pendingUpdate, progress =>
                    Dispatcher.InvokeAsync(() => UpdateButton.Content = L.T("Downloading {0}%", progress)));
                updateDownloaded = true;
            }
            if (loading is not null)
            {
                MessageBox.Show(this, L.T("Wait for the current load to finish, then restart to install the update."), L.T("Update ready"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string version = pendingUpdate.TargetFullRelease.Version.ToString();
            if (MessageBox.Show(this, L.T("Version {0} is ready. Restart the editor to install it?", version), L.T("Update ready"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (loading is not null || !CanReplace()) return;
            updater!.ApplyUpdatesAndRestart(pendingUpdate);
        }
        catch (Exception ex) { Error(new Exception(L.T("Update failed: {0}", ex.Message), ex)); }
        finally
        {
            updateBusy = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = updateDownloaded ? L.T("Restart to update") : L.T("Download update");
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateBusy || pendingUpdate is not null) return;
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = L.T("Checking for updates…");
        try
        {
            // Prerelease builds follow prerelease releases; stable builds only ever move to stable releases.
            updater ??= new UpdateManager(new GithubSource(UpdateRepository, accessToken: null, prerelease: BuildVersion.Contains('-')));
            if (!updater.IsInstalled)
            {
                if (manual) MessageBox.Show(this, L.T("Automatic updates require the installed editor (Setup.exe) or the updatable portable package. Download the latest release from {0}/releases", UpdateRepository),
                    L.T("Portable or development build"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            pendingUpdate = await updater.CheckForUpdatesAsync();
            if (pendingUpdate is not null)
                Notify(L.T("Update available"), L.T("Version {0} is available. Use the Download button in the toolbar to install it.", pendingUpdate.TargetFullRelease.Version));
            else if (manual)
                MessageBox.Show(this, L.T("You are running the latest version ({0}).", updater.CurrentVersion), L.T("Updates"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            string reason = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                ? L.T("The release feed is not publicly accessible (GitHub 404). Updates are checked without GitHub credentials, so the repository and its releases must be public.")
                : ex.Message;
            if (manual) Error(new Exception(L.T("Could not check for updates: {0}", reason), ex));
            else Diagnostics.Text += "\n" + L.T("Update check unavailable: {0}", reason);
        }
        finally
        {
            updateBusy = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = pendingUpdate is null ? L.T("Check for updates") : L.T("Download {0}", pendingUpdate.TargetFullRelease.Version);
            UpdateButton.ToolTip = L.T("Installed version: {0}", updater?.IsInstalled == true ? updater.CurrentVersion?.ToString() ?? BuildVersion : BuildVersion);
        }
    }
}
