using System.Text.Json;

namespace D2RLevel.Core;

public sealed record EditorWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record EditorSettings(string? AssetFolder = null, string? GrannyPath = null, Dictionary<string, string>? PresetPairs = null, EditorWindowPlacement? WindowPlacement = null, string? WorkspaceFolder = null, string[]? RecentWorkspaces = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "D2RLevelEditor", "settings.json");

    public static EditorSettings Load(string path, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Expected a settings object.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            warning = "Could not read saved editor settings: " + ex.Message;
            return new();
        }
    }

    public void Save(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
