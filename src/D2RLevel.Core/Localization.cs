using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace D2RLevel.Core;

/// <summary>
/// One translated UI language: the English source text is the key, the translation is the value.
/// Files live in the application's <c>lang</c> folder as <c>&lt;code&gt;.json</c> so contributors can
/// add a language by copying <c>en.json</c>; nothing needs recompiling. An empty value means
/// "not translated yet" and falls back to the English text, so partial translations still ship.
/// </summary>
public sealed class LanguagePack
{
    public const string LanguageNameKey = "$language";
    public const string TemplateFileName = "en.json";

    public string Code { get; }
    public string DisplayName { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Entries { get; }
    /// <summary>Other <c>$</c>-prefixed keys (notes, credits) carried through untouched when files are regenerated.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    private LanguagePack(string code, string displayName, string path, Dictionary<string, string> entries, Dictionary<string, string> metadata)
    { Code = code; DisplayName = displayName; Path = path; Entries = entries; Metadata = metadata; }

    public static string CodeFor(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    public static LanguagePack Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("Expected a JSON object of \"English text\": \"translation\" pairs.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        foreach (var (key, value) in root)
        {
            if (value is not JsonValue json || !json.TryGetValue<string>(out var text))
                throw new InvalidDataException($"\"{key}\" must be a string.");
            if (key == LanguageNameKey) name = text;
            else if (key.StartsWith('$')) metadata[key] = text;
            else entries[key] = text;
        }
        return new(CodeFor(path), string.IsNullOrWhiteSpace(name) ? CodeFor(path) : name!.Trim(), path, entries, metadata);
    }

    /// <summary>Every language file next to the English template, excluding the template itself.</summary>
    public static IReadOnlyList<string> Discover(string directory) => !Directory.Exists(directory) ? [] :
        Directory.EnumerateFiles(directory, "*.json").Where(f => !System.IO.Path.GetFileName(f).Equals(TemplateFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>
/// Process-wide translation lookup. English text is both the key and the fallback, so code stays readable
/// and a missing language file, missing entry or empty entry never hides UI text.
/// </summary>
public static class Translations
{
    public const string EnglishCode = "en";
    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    public static LanguagePack? Active { get; private set; }
    public static string ActiveCode => Active?.Code ?? EnglishCode;

    public static void Use(LanguagePack? pack) => Active = pack;

    /// <summary>
    /// Picks the language file to load: an explicit request wins, then the OS display language (by
    /// full tag, then by two-letter code), then English. Returns null when English should be used.
    /// </summary>
    public static string? Choose(string directory, string? requested, CultureInfo? culture = null)
    {
        var files = LanguagePack.Discover(directory);
        string? Match(string? code) => code is null || code.Equals(EnglishCode, StringComparison.OrdinalIgnoreCase) ? null :
            files.FirstOrDefault(f => LanguagePack.CodeFor(f).Equals(code, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(requested)) return Match(requested.Trim());
        culture ??= CultureInfo.CurrentUICulture;
        return Match(culture.Name.ToLowerInvariant()) ?? Match(culture.TwoLetterISOLanguageName);
    }

    public static string Translate(string english, params object?[] args)
    {
        string text = english;
        if (Active is { } pack && pack.Entries.TryGetValue(english, out var translated) && translated.Length > 0)
        {
            // A translation with a different placeholder set would throw or drop values at runtime;
            // the audit rejects those in CI, and this keeps the app upright when a hand-edited file slips through.
            if (args.Length == 0 || PlaceholdersMatch(english, translated)) text = translated;
        }
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public static string[] Placeholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(int.Parse).ToArray();

    public static bool PlaceholdersMatch(string english, string translated) =>
        Placeholders(english).SequenceEqual(Placeholders(translated));
}

/// <summary>
/// Keeps language files honest against the source code: the template must list exactly the strings the
/// app can show, every translation must only contain known strings, and placeholders must line up.
/// Runs in the editor checks and in CI so a contributed translation cannot silently drift.
/// </summary>
public static class TranslationsAudit
{
    private static readonly Regex CSharpCall = new(@"\bL\.T\(\s*(\$|@|\$@|@\$)?""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex XamlCall = new(@"\{l:T\s+(?:'((?:[^'\\]|\\.)*)'|([^{}',][^{}]*?))\s*\}", RegexOptions.Compiled);

    public sealed record Result(IReadOnlyList<string> Failures, IReadOnlyList<string> Report, SortedSet<string> SourceStrings);

    public static string? FindRepositoryRoot(params string[] startPoints)
    {
        foreach (var start in startPoints)
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "D2RLevelEditor.slnx"))) return dir.FullName;
        return null;
    }

    public static string AppDirectory(string repoRoot) => Path.Combine(repoRoot, "src", "D2RLevel.App");
    public static string LanguageDirectory(string repoRoot) => Path.Combine(AppDirectory(repoRoot), "lang");

    /// <summary>Every English string passed to <c>L.T("…")</c> in C# or <c>{l:T '…'}</c> in XAML.</summary>
    public static SortedSet<string> ExtractSourceStrings(string appDirectory, List<string>? failures = null)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.TopDirectoryOnly).Concat(Directory.EnumerateFiles(appDirectory, "*.xaml")))
        {
            // Comments are stripped so documentation that quotes L.T("…") does not become a translatable string.
            string text = Regex.Replace(File.ReadAllText(file), @"^\s*//.*$", "", RegexOptions.Multiline), name = Path.GetFileName(file);
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in CSharpCall.Matches(text))
                {
                    if (m.Groups[1].Success)
                    { failures?.Add($"{name}: L.T must take a plain string literal so the audit can extract it (found {m.Groups[1].Value}\"…\"). Use placeholders: L.T(\"Moved {{0}}\", value)."); continue; }
                    strings.Add(UnescapeCSharp(m.Groups[2].Value));
                }
                foreach (Match m in Regex.Matches(text, @"\bL\.T\(\s*(?!\$?@?\$?"")[A-Za-z_]"))
                    failures?.Add($"{name}: L.T must take a string literal, not a variable, so the audit can extract it (near offset {m.Index}).");
            }
            else foreach (Match m in XamlCall.Matches(text))
                strings.Add(UnescapeXaml(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim()));
        }
        return strings;
    }

    public static Result Run(string repoRoot)
    {
        var failures = new List<string>();
        var report = new List<string>();
        var source = ExtractSourceStrings(AppDirectory(repoRoot), failures);
        string langDir = LanguageDirectory(repoRoot), templatePath = Path.Combine(langDir, LanguagePack.TemplateFileName);
        if (!File.Exists(templatePath)) failures.Add($"Missing {templatePath}. Build the app or run the checks with --strings-update to generate it.");
        else
        {
            var template = LanguagePack.Load(templatePath);
            var missing = source.Except(template.Entries.Keys).ToArray();
            var stale = template.Entries.Keys.Except(source).ToArray();
            foreach (var s in missing) failures.Add($"en.json lacks source string: {Quote(s)}");
            foreach (var s in stale) failures.Add($"en.json lists a string the app no longer uses: {Quote(s)}");
            if (missing.Length + stale.Length > 0) failures.Add("Build the app (or run `dotnet run --project tests/D2RLevel.Tests -- --strings-update`) to sync en.json and every language file, then commit them.");
            foreach (var (key, value) in template.Entries)
                if (value.Length > 0) failures.Add($"en.json values must stay empty; the key is the English text: {Quote(key)}");
        }
        foreach (var file in LanguagePack.Discover(langDir))
        {
            string name = Path.GetFileName(file);
            LanguagePack pack;
            try { pack = LanguagePack.Load(file); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { failures.Add($"{name}: {ex.Message}"); continue; }
            if (!Regex.IsMatch(LanguagePack.CodeFor(file), @"^[a-z]{2,3}(-[a-z0-9]{2,8})*$"))
                failures.Add($"{name}: name language files by IETF tag, e.g. de.json or pt-br.json.");
            if (pack.DisplayName == pack.Code) failures.Add($"{name}: set \"{LanguagePack.LanguageNameKey}\" to the language's own name, e.g. \"Deutsch\".");
            int translated = 0;
            foreach (var (key, value) in pack.Entries)
            {
                if (!source.Contains(key)) { failures.Add($"{name}: entry no longer exists in the app (run --strings-update, then re-translate if the wording changed): {Quote(key)}"); continue; }
                if (value.Length == 0) continue;
                translated++;
                if (!Translations.PlaceholdersMatch(key, value))
                    failures.Add($"{name}: placeholders differ from the English text (expected {{{string.Join("}, {", Translations.Placeholders(key))}}}): {Quote(key)}");
                if (key.Contains('_') && Regex.IsMatch(key, @"^_[A-Za-z]") && !value.Contains('_'))
                    report.Add($"{name}: menu shortcut underscore dropped in {Quote(key)} (optional).");
            }
            foreach (var key in source.Except(pack.Entries.Keys)) report.Add($"{name}: missing entry (falls back to English): {Quote(key)}");
            report.Insert(0, $"{name} ({pack.DisplayName}): {translated}/{source.Count} strings translated ({(source.Count == 0 ? 0 : 100 * translated / source.Count)}%).");
        }
        return new(failures, report, source);
    }

    /// <summary>
    /// Rewrites en.json from source and adds any new strings (empty) to every language file, dropping
    /// entries the app no longer uses. Existing translations and key order are preserved; new keys append
    /// in source order so a diff shows exactly what a translator needs to fill in.
    /// </summary>
    public static IReadOnlyList<string> Update(string repoRoot)
    {
        var log = new List<string>();
        var failures = new List<string>();
        var source = ExtractSourceStrings(AppDirectory(repoRoot), failures);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        string langDir = LanguageDirectory(repoRoot);
        Directory.CreateDirectory(langDir);
        var english = File.Exists(Path.Combine(langDir, LanguagePack.TemplateFileName)) ? LanguagePack.Load(Path.Combine(langDir, LanguagePack.TemplateFileName)) : null;
        Write(Path.Combine(langDir, LanguagePack.TemplateFileName), "English", english?.Metadata, source.Select(s => (s, "")), log);
        foreach (var file in LanguagePack.Discover(langDir))
        {
            var pack = LanguagePack.Load(file);
            var kept = pack.Entries.Where(e => source.Contains(e.Key)).Select(e => (e.Key, e.Value)).ToList();
            foreach (var stale in pack.Entries.Keys.Where(k => !source.Contains(k)))
                log.Add($"{Path.GetFileName(file)}: removed unused entry {Quote(stale)} (translation was {Quote(pack.Entries[stale])})");
            kept.AddRange(source.Where(s => !pack.Entries.ContainsKey(s)).Select(s => (s, "")));
            Write(file, pack.DisplayName == pack.Code ? "" : pack.DisplayName, pack.Metadata, kept, log);
        }
        return log;
    }

    private static void Write(string path, string displayName, IReadOnlyDictionary<string, string>? metadata, IEnumerable<(string Key, string Value)> entries, List<string> log)
    {
        var json = new JsonObject { [LanguagePack.LanguageNameKey] = displayName };
        foreach (var (key, value) in metadata ?? new Dictionary<string, string>()) json[key] = value;
        int added = 0, total = 0;
        foreach (var (key, value) in entries) { json[key] = value; total++; if (value.Length == 0) added++; }
        string text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine;
        if (File.Exists(path) && File.ReadAllText(path) == text) { log.Add($"{Path.GetFileName(path)}: unchanged"); return; }
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        log.Add($"{Path.GetFileName(path)}: written, {total} entries, {added} untranslated");
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    private static string UnescapeCSharp(string literal) => Regex.Replace(literal, @"\\(u[0-9A-Fa-f]{4}|.)", m => m.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", "\"" => "\"", "\\" => "\\", "0" => "\0",
        var u when u.Length == 5 => ((char)Convert.ToInt32(u[1..], 16)).ToString(),
        var other => other,
    });

    private static string UnescapeXaml(string text) => Regex.Replace(text, @"\\(.)", "$1")
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&#10;", "\n").Replace("&amp;", "&");
}
