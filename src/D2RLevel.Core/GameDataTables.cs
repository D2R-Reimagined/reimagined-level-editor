using System.Collections.ObjectModel;

namespace D2RLevel.Core;

public sealed record GameDataRow(IReadOnlyDictionary<string, string> Fields, string SourcePath, int Line)
{
    public string this[string column] => Fields.GetValueOrDefault(column, "");
}

public sealed record GameDataTable(string Name, string? SourcePath, bool IsOverride,
    IReadOnlyList<GameDataRow> Rows, string? Warning);

/// <summary>One read-only snapshot per table. An existing mod file replaces the whole base table.</summary>
public sealed class GameDataTables(AssetResolver? assets, string? workspaceDataRoot)
{
    private readonly Dictionary<string, GameDataTable> tables = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<GameDataTable> Loaded => tables.Values.ToArray();

    public GameDataTable Read(string name)
    {
        if (tables.TryGetValue(name, out var cached)) return cached;
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid table name.", nameof(name));
        string? source = null;
        bool isOverride = false;
        try
        {
            string? fallback = assets?.Resolve($"data/global/excel/{name}.txt");
            string? local = workspaceDataRoot is null ? null : Path.GetFullPath(Path.Combine(workspaceDataRoot, "global", "excel", name + ".txt"));
            isOverride = local is not null && File.Exists(local) && !string.Equals(local, fallback, StringComparison.OrdinalIgnoreCase);
            source = local is not null && File.Exists(local) ? local : fallback;
            if (source is null || !File.Exists(source))
                return tables[name] = new(name, source, isOverride, [], "Table unavailable.");
            var lines = File.ReadAllLines(source);
            if (lines.Length == 0) throw new InvalidDataException("Table is empty.");
            var columns = lines[0].TrimStart('\uFEFF').Split('\t');
            var named = columns.Where(c => c.Length > 0).ToArray();
            if (named.Length == 0 || named.Distinct(StringComparer.OrdinalIgnoreCase).Count() != named.Length)
                throw new InvalidDataException("Table header is invalid or contains duplicate columns.");
            var rows = new List<GameDataRow>();
            for (int line = 1; line < lines.Length; line++)
            {
                if (string.IsNullOrWhiteSpace(lines[line])) continue;
                var cells = lines[line].Split('\t');
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int col = 0; col < columns.Length; col++)
                    if (columns[col].Length > 0) fields.Add(columns[col], col < cells.Length ? cells[col] : "");
                rows.Add(new(new ReadOnlyDictionary<string, string>(fields), source, line + 1));
            }
            return tables[name] = new(name, source, isOverride, rows.AsReadOnly(), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return tables[name] = new(name, source, isOverride, [], ex.Message); }
    }
}
