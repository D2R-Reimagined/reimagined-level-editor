using System.IO;
using System.Windows.Markup;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>
/// UI text goes through <c>L.T("English text")</c>. The English string is the lookup key, so wrap
/// the literal in place and keep the code readable; values come from <c>lang/&lt;code&gt;.json</c>
/// beside the executable. Always pass a plain literal (placeholders as <c>{0}</c>, not <c>$"…"</c>):
/// the editor checks extract those literals to keep the translation template in sync.
/// </summary>
internal static class L
{
    public static string LanguageDirectory => Path.Combine(AppContext.BaseDirectory, "lang");

    public static string T(string english, params object?[] args) => Translations.Translate(english, args);

    /// <summary>Loads the language chosen by <c>--language</c>, then settings, then the OS display language.</summary>
    public static string? Initialize(string? requested, out string? warning)
    {
        warning = null;
        string? file = Translations.Choose(LanguageDirectory, requested);
        if (file is null) { Translations.Use(null); return null; }
        try { Translations.Use(LanguagePack.Load(file)); return Translations.ActiveCode; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Translations.Use(null);
            warning = $"Language file {Path.GetFileName(file)} could not be read, so the editor stays in English: {ex.Message}";
            return null;
        }
    }

    /// <summary>Installed language files, as (code, display name) — for the language menu.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)>();
        foreach (var file in LanguagePack.Discover(LanguageDirectory))
        {
            try { var pack = LanguagePack.Load(file); list.Add((pack.Code, pack.DisplayName)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            { list.Add((LanguagePack.CodeFor(file), LanguagePack.CodeFor(file) + " (unreadable)")); }
        }
        return list;
    }
}

/// <summary>XAML counterpart of <see cref="L.T"/>: <c>Header="{l:T 'Open preset…'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string text) => Text = text;
    [ConstructorArgument("text")] public string Text { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Translations.Translate(Text);
}
