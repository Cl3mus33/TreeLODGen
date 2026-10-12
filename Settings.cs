using System.Text.Json;

namespace TreeLODGen;

// What was in the window at the last Generate, so that nothing has to be typed again.
// Kept per Mod Organizer 2 instance (its mods folder), since each has its own output
// folder and may use another game or DynDOLOD copy; one more entry serves launches made
// outside Mod Organizer 2.
sealed class Settings
{
    public sealed class Entry
    {
        public string Data { get; set; } = "";
        public string Output { get; set; } = "";
        public string DynDOLOD { get; set; } = "";
        public int Detail { get; set; } = 1;
        public bool All { get; set; }
        public bool Far3d { get; set; }
    }

    public Dictionary<string, Entry> Launchers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TreeLODGen", "settings.json");
    static string Key(string? modsFolder) => string.IsNullOrEmpty(modsFolder) ? "(no mod manager)" : Path.GetFullPath(modsFolder).TrimEnd('\\');

    public static Entry? Load(string? modsFolder)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var all = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            return all != null && all.Launchers.TryGetValue(Key(modsFolder), out var entry) ? entry : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // unreadable settings are the same as none
        }
    }

    public static void Save(string? modsFolder, Entry entry)
    {
        try
        {
            Settings all;
            try { all = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings() : new Settings(); }
            catch (JsonException) { all = new Settings(); }
            all.Launchers = new Dictionary<string, Entry>(all.Launchers, StringComparer.OrdinalIgnoreCase) { [Key(modsFolder)] = entry };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the run goes on without remembering its settings
        }
    }
}
