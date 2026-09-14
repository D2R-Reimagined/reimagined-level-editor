using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>
    /// View → Language lists English plus every file in the lang folder. The choice is saved and takes
    /// effect on the next start: the panels are built in code, so re-labelling a live window would mean
    /// rebuilding it around an open workspace.
    /// </summary>
    private void PopulateLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        string active = Translations.ActiveCode;
        string? chosen = Argument("--language") ?? settings.Language;
        void Add(string code, string name)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = code.Equals(active, StringComparison.OrdinalIgnoreCase), Tag = code };
            item.Click += (_, _) => ChooseLanguage(code);
            LanguageMenu.Items.Add(item);
        }
        Add(Translations.EnglishCode, "English");
        foreach (var (code, name) in L.Available()) Add(code, name);
        LanguageMenu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var automatic = new MenuItem { Header = L.T("Follow Windows display language"), IsCheckable = true, IsChecked = string.IsNullOrEmpty(chosen), Tag = "" };
        automatic.Click += (_, _) => ChooseLanguage(null);
        LanguageMenu.Items.Add(automatic);
        var folder = new MenuItem { Header = L.T("Open language folder…"), ToolTip = L.LanguageDirectory };
        folder.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(L.LanguageDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(L.LanguageDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { Error(ex); }
        };
        LanguageMenu.Items.Add(folder);
    }

    private void ChooseLanguage(string? code)
    {
        foreach (var item in LanguageMenu.Items.OfType<MenuItem>().Where(i => i.Tag is string))
            item.IsChecked = (string)item.Tag! == (code ?? "");
        settings = settings with { Language = code };
        if (SaveSettings() is { } warning) { Status.Text = warning; return; }
        string? file = Translations.Choose(L.LanguageDirectory, code);
        bool unchanged = (file is null ? Translations.EnglishCode : LanguagePack.CodeFor(file)).Equals(Translations.ActiveCode, StringComparison.OrdinalIgnoreCase);
        Status.Text = unchanged ? L.T("Language preference saved.") : L.T("Language preference saved. Restart the editor to apply it.");
        if (!unchanged) Notify(L.T("Language"), L.T("Restart the editor to apply the new language."));
    }
}
