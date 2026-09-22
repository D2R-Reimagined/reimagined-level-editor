using System.Text;

namespace D2RLevel.Core;

/// <summary>Numeric connection edits preserve all other bytes, row order, encoding, BOM and line endings.</summary>
public sealed class LevelTableEdits
{
    private byte[] bytes, saved;
    private readonly byte[] sourceSnapshot;
    private byte[]? targetSnapshot;
    private readonly EditHistory history;
    private readonly Dictionary<string, byte[]?> witnesses = new(StringComparer.OrdinalIgnoreCase);
    public string SourcePath { get; }
    public string TargetPath { get; }
    public bool IsDirty => !bytes.SequenceEqual(saved);
    public LevelTableEdits(string source, string root, EditHistory history)
    {
        SourcePath = Path.GetFullPath(source); TargetPath = SceneWorkspace.Inside(root, Path.Combine(root, "global", "excel", "levels.txt"));
        this.history = history; bytes = File.ReadAllBytes(SourcePath); saved = bytes.ToArray(); sourceSnapshot = bytes.ToArray();
        targetSnapshot = File.Exists(TargetPath) ? File.ReadAllBytes(TargetPath) : null;
        if (targetSnapshot is not null && !SourcePath.Equals(TargetPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A workspace Levels override already exists. Refresh before editing connections.");
        _ = Table;
    }
    private List<(int Start, int Length)[]> Lines()
    {
        var result = new List<(int, int)[]>(); int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != 10) continue;
            int end = i > start && bytes[i - 1] == 13 ? i - 1 : i;
            var cells = new List<(int, int)>(); int cell = start;
            for (int j = start; j <= end; j++) if (j == end || bytes[j] == 9) { cells.Add((cell, j - cell)); cell = j + 1; }
            result.Add(cells.ToArray()); start = i + 1;
        }
        return result;
    }
    private string Text((int Start, int Length) span) => Encoding.UTF8.GetString(bytes, span.Start, span.Length);
    public GameDataTable Table
    {
        get
        {
            var lines = Lines(); var columns = lines[0].Select(Text).ToArray(); columns[0] = columns[0].TrimStart('\uFEFF');
            if (!columns.Contains("Id") || columns.Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count(c => c.Length > 0))
                throw new InvalidDataException("Invalid Levels table header.");
            var rows = new List<GameDataRow>();
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].All(c => c.Length == 0)) continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < columns.Length; c++) if (columns[c].Length > 0) values.Add(columns[c], c < lines[i].Length ? Text(lines[i][c]) : "");
                rows.Add(new(values, SourcePath, i + 1));
            }
            return new("levels", SourcePath, true, rows, null);
        }
    }
    public void Witness(string path)
    {
        path = Path.GetFullPath(path); var data = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (witnesses.TryGetValue(path, out var before) && !Equal(data, before)) throw new IOException("Connection dependency changed outside the editor: " + path);
        witnesses[path] = data;
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
    public void VerifyUnchanged()
    {
        byte[]? current = File.Exists(TargetPath) ? File.ReadAllBytes(TargetPath) : null;
        if (current is null ? targetSnapshot is not null : targetSnapshot is null || !current.SequenceEqual(targetSnapshot))
            throw new IOException("Levels changed outside the editor. Reopen the scene before saving connections.");
        if (!SourcePath.Equals(TargetPath, StringComparison.OrdinalIgnoreCase) && !File.ReadAllBytes(SourcePath).SequenceEqual(sourceSnapshot))
            throw new IOException("Base Levels changed outside the editor. Reopen before saving connections.");
        foreach (var (path, data) in witnesses) if (!Equal(File.Exists(path) ? File.ReadAllBytes(path) : null, data)) throw new IOException("Connection dependency changed outside the editor: " + path);
    }
    public void Apply(IReadOnlyList<(int Area, int Slot, int Destination)> routes)
    {
        VerifyUnchanged(); var table = Table; var lines = Lines(); var headers = lines[0].Select(Text).ToArray();
        var changes = new List<(int Start, int Length, byte[] Value)>();
        foreach (var route in routes)
        {
            if (route.Slot is < 0 or > 7 || route.Destination <= 0) throw new InvalidDataException("Invalid connection destination or slot.");
            var row = table.Rows.Single(r => r["Id"] == route.Area.ToString()); int column = Array.IndexOf(headers, "Vis" + route.Slot);
            if (column < 0 || column >= lines[row.Line - 1].Length) throw new InvalidDataException("Connection cell is missing from Levels.");
            var cell = lines[row.Line - 1][column]; changes.Add((cell.Start, cell.Length, Encoding.ASCII.GetBytes(route.Destination.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        if (changes.Select(c => c.Start).Distinct().Count() != changes.Count) throw new InvalidDataException("Duplicate connection edit.");
        var before = bytes.ToArray(); var output = bytes.ToList();
        foreach (var c in changes.OrderByDescending(c => c.Start)) { output.RemoveRange(c.Start, c.Length); output.InsertRange(c.Start, c.Value); }
        var after = output.ToArray(); if (before.SequenceEqual(after)) return;
        bytes = after; history.Record(() => bytes = before, () => bytes = after, this);
    }
    public byte[] Serialize() => bytes.ToArray();
    internal byte[]? TargetSnapshot => targetSnapshot?.ToArray();
    internal void MarkSaved(string scenePath, byte[] sceneBytes)
    {
        if (IsDirty) { saved = bytes.ToArray(); targetSnapshot = bytes.ToArray(); }
        if (witnesses.ContainsKey(scenePath)) witnesses[scenePath] = sceneBytes.ToArray();
    }
}
