namespace TreeLODGen;

// The game's view of the Data folder: loose files in priority order, then every archive.
// Built either from a Mod Organizer 2 profile (mods read straight from disk) or from the
// Data folder alone, which is already complete when the tool is started through a mod
// manager's virtual file system or when mods are deployed into Data.
sealed class Vfs
{
    readonly Dictionary<string, (string path, string source)> _loose = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(Bsa bsa, string source)> _archives = new();
    readonly string _cacheDir;

    public int LooseCount => _loose.Count;
    public int ArchiveCount => _archives.Count;

    Vfs(IEnumerable<(string path, string source)> roots, string cacheDir, IReadOnlyList<string> loadOrder, Action<int>? progress)
    {
        _cacheDir = cacheDir;
        var seen = 0;
        foreach (var (root, source) in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                _loose.TryAdd(Path.GetRelativePath(root, file), (file, source));
                if (++seen % 2000 == 0) progress?.Invoke(seen);
            }
            foreach (var archive in Directory.EnumerateFiles(root, "*.bsa"))
            {
                try { _archives.Add((new Bsa(archive), $"{Path.GetFileName(archive)} ({source})")); }
                catch (InvalidDataException) { /* older archive formats hold nothing a tree needs */ }
            }
        }

        // Between archives the game lets the one of the later plugin win ("Plugin.bsa",
        // "Plugin - Textures.bsa"); the base game's own archives come last.
        int Rank(string archivePath)
        {
            var name = Path.GetFileNameWithoutExtension(archivePath);
            for (var i = loadOrder.Count - 1; i >= 0; i--)
                if (name.StartsWith(Path.GetFileNameWithoutExtension(loadOrder[i]), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        var ranked = _archives.Select((a, index) => (a, rank: Rank(a.bsa.ArchivePath), index)).OrderByDescending(x => x.rank).ThenBy(x => x.index).Select(x => x.a).ToList();
        _archives.Clear();
        _archives.AddRange(ranked);
    }

    public static Vfs FromDataFolder(string gameData, string cacheDir, IReadOnlyList<string> loadOrder, Action<int>? progress = null) => new(new[] { (gameData, "Data") }, cacheDir, loadOrder, progress);

    public static Vfs FromMo2Profile(string instanceDir, string profile, string gameData, string cacheDir, IReadOnlyList<string> loadOrder, Func<string, bool> excludeMod, Action<int>? progress = null)
    {
        // modlist.txt lists the highest priority mod first
        var roots = File.ReadAllLines(Path.Combine(instanceDir, "profiles", profile, "modlist.txt"))
            .Where(l => l.StartsWith('+'))
            .Select(l => l[1..])
            .Where(m => !excludeMod(m))
            .Select(m => (path: Path.Combine(instanceDir, "mods", m), source: m))
            .Where(r => Directory.Exists(r.path))
            .ToList();
        roots.Add((gameData, "game Data"));
        return new Vfs(roots, cacheDir, loadOrder, progress);
    }

    // Where a file comes from, without extracting it.
    public string? SourceOf(string relativePath)
    {
        if (_loose.TryGetValue(relativePath, out var loose)) return loose.source;
        return _archives.FirstOrDefault(a => a.bsa.Contains(relativePath)).source;
    }

    // A real path on disk for the file; archive content is extracted to the cache once.
    public string? Resolve(string relativePath)
    {
        if (_loose.TryGetValue(relativePath, out var loose)) return loose.path;
        var archive = _archives.FirstOrDefault(a => a.bsa.Contains(relativePath)).bsa;
        if (archive == null) return null;

        var cached = Path.Combine(_cacheDir, relativePath.ToLowerInvariant());
        if (!File.Exists(cached))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllBytes(cached, archive.Extract(relativePath));
        }
        return cached;
    }

    // Relative paths (with their source) of everything under a folder, loose or archived.
    public IEnumerable<(string path, string source)> FilesUnder(string relativeFolder)
    {
        var prefix = relativeFolder.TrimEnd('\\') + "\\";
        foreach (var (path, entry) in _loose)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return (path, entry.source);
        foreach (var (bsa, source) in _archives)
            foreach (var path in bsa.Files)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return (path, source);
    }
}
