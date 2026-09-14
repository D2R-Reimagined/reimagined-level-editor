using System.Globalization;
using D2RLevel.Core;

/// <summary>Language pack loading, fallback and the source-to-template audit that keeps contributed translations honest.</summary>
internal static class LocalizationChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        var langDir = Path.Combine(folder, "lang"); Directory.CreateDirectory(langDir);
        File.WriteAllText(Path.Combine(langDir, "en.json"), "{ \"$language\": \"English\", \"Lock terrain\": \"\", \"Moved {0} assets\": \"\" }");
        File.WriteAllText(Path.Combine(langDir, "de.json"), "{ \"$language\": \"Deutsch\", \"Lock terrain\": \"Terrain sperren\", \"Moved {0} assets\": \"\", \"Undo\": \"Rückgängig\", \"Bad {0}\": \"Schlecht {1}\" }");
        File.WriteAllText(Path.Combine(langDir, "pt-br.json"), "{ \"$language\": \"Português (Brasil)\" }");
        File.WriteAllText(Path.Combine(langDir, "broken.json"), "{ \"$language\": 5 }");

        var pack = LanguagePack.Load(Path.Combine(langDir, "de.json"));
        check(pack.Code == "de" && pack.DisplayName == "Deutsch" && pack.Entries.Count == 4 && !pack.Entries.ContainsKey("$language"), "language pack reads code, display name and entries");
        throws(() => LanguagePack.Load(Path.Combine(langDir, "broken.json")), "language pack rejects non-string values");
        check(LanguagePack.Discover(langDir).Select(Path.GetFileName).SequenceEqual(["broken.json", "de.json", "pt-br.json"]), "discovery lists language files without en.json");
        check(LanguagePack.Discover(Path.Combine(folder, "missing")).Count == 0, "discovery tolerates a missing folder");

        Translations.Use(null);
        check(Translations.Translate("Lock terrain") == "Lock terrain" && Translations.ActiveCode == "en", "English is the fallback with no pack");
        Translations.Use(pack);
        check(Translations.Translate("Lock terrain") == "Terrain sperren", "translation replaces English text");
        check(Translations.Translate("Moved {0} assets", 3) == "Moved 3 assets", "empty translation falls back to English with formatting");
        check(Translations.Translate("Not in file {0}", 7) == "Not in file 7", "missing entry falls back to English");
        check(Translations.Translate("Bad {0}", 1) == "Bad 1", "mismatched placeholders fall back to English instead of throwing");
        check(Translations.Translate("Undo") == "Rückgängig", "non-ASCII translations survive");
        Translations.Use(null);

        check(Translations.Choose(langDir, "de") is { } deFile && LanguagePack.CodeFor(deFile) == "de", "explicit request picks its file");
        check(Translations.Choose(langDir, "DE") is not null, "explicit request is case-insensitive");
        check(Translations.Choose(langDir, "en") is null, "explicit English uses no file");
        check(Translations.Choose(langDir, "xx") is null, "unknown request falls back to English rather than another language");
        check(Translations.Choose(langDir, null, new CultureInfo("pt-BR")) is { } pt && LanguagePack.CodeFor(pt) == "pt-br", "OS culture matches a regional file by full tag");
        check(Translations.Choose(langDir, null, new CultureInfo("de-AT")) is { } at && LanguagePack.CodeFor(at) == "de", "OS culture falls back to the two-letter language");
        check(Translations.Choose(langDir, null, new CultureInfo("fr-FR")) is null, "OS culture without a file stays English");
        check(Translations.Placeholders("{1} then {0:F1} and {0}").SequenceEqual(["0", "1"]), "placeholder extraction ignores format specifiers and duplicates");

        // A miniature app tree proves extraction and the audit end to end, independent of the real sources.
        var repo = Path.Combine(folder, "repo"); var app = Path.Combine(repo, "src", "D2RLevel.App"); Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(repo, "D2RLevelEditor.slnx"), "");
        File.WriteAllText(Path.Combine(app, "A.cs"), "var a = L.T(\"Lock terrain\"); var b = L.T(\"Moved {0} assets\", n); var c = L.T(\"Quote \\\" and\\nnewline\");");
        File.WriteAllText(Path.Combine(app, "B.xaml"), "<MenuItem Header=\"{l:T '_File'}\" ToolTip=\"{l:T 'It\\'s here &amp; there'}\"/>");
        var extracted = TranslationsAudit.ExtractSourceStrings(app);
        check(extracted.SetEquals(["Lock terrain", "Moved {0} assets", "Quote \" and\nnewline", "_File", "It's here & there"]), "audit extracts C# and XAML literals with escapes");
        check(TranslationsAudit.FindRepositoryRoot(app) == repo, "audit locates the repository root from a nested folder");

        var initial = TranslationsAudit.Run(repo);
        check(initial.Failures.Any(f => f.Contains("Missing")), "audit fails without a template");
        TranslationsAudit.Update(repo);
        var template = LanguagePack.Load(Path.Combine(TranslationsAudit.LanguageDirectory(repo), "en.json"));
        check(template.Entries.Count == 5 && template.Entries.Values.All(v => v == ""), "update writes an empty template for every source string");
        check(TranslationsAudit.Run(repo).Failures.Count == 0, "fresh template passes the audit");

        var frPath = Path.Combine(TranslationsAudit.LanguageDirectory(repo), "fr.json");
        File.WriteAllText(frPath, "{ \"$language\": \"Français\", \"$note\": \"draft\", \"Lock terrain\": \"Verrouiller le terrain\", \"Moved {0} assets\": \"{1} déplacés\", \"Gone\": \"Parti\" }");
        var audit = TranslationsAudit.Run(repo);
        check(audit.Failures.Any(f => f.Contains("placeholders")) && audit.Failures.Any(f => f.Contains("no longer exists")), "audit flags placeholder mismatch and stale entries");
        check(audit.Report.Any(r => r.Contains("fr.json (Français): 2/5")), "audit reports coverage per language");
        File.WriteAllText(Path.Combine(app, "C.cs"), "var d = L.T($\"Interpolated {x}\"); var e = L.T(variable);");
        check(TranslationsAudit.Run(repo).Failures.Count(f => f.Contains("literal")) == 2, "audit rejects interpolated and non-literal L.T calls");
        File.Delete(Path.Combine(app, "C.cs"));
        File.WriteAllText(Path.Combine(app, "A.cs"), "var a = L.T(\"Lock terrain\"); var b = L.T(\"New text\");");
        var log = TranslationsAudit.Update(repo);
        var fr = LanguagePack.Load(frPath);
        check(fr.Entries["Lock terrain"] == "Verrouiller le terrain" && fr.Entries.ContainsKey("New text") && !fr.Entries.ContainsKey("Gone") && fr.DisplayName == "Français" && fr.Metadata["$note"] == "draft", "update keeps translations, metadata and adds new strings, dropping unused ones");
        check(log.Any(l => l.Contains("removed unused entry") && l.Contains("Parti")), "update logs the translation it dropped");
        File.WriteAllText(frPath, "{ \"$language\": \"Français\", \"Lock terrain\": \"Verrouiller le terrain\", \"New text\": \"\", \"_File\": \"Fichier\" }");
        var final = TranslationsAudit.Run(repo);
        check(final.Failures.Count == 0 && final.Report.Any(r => r.Contains("underscore")), "clean file passes; dropped menu shortcut is a note, not a failure");
    }
}
