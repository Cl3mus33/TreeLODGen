using System.Text.Json;

namespace TreeLODGen;

// The DynDOLOD rules written for the generated trees, one per tree, and the per-tree choice
// behind each: the 3D model at LOD 4 and billboards beyond, the 3D models at every level, or
// billboards only. Choices are kept apart from the models, so changing them only rewrites
// the rule files: the trees are not generated again (DynDOLOD has to run again, though).
static class TreeRules
{
    public const string Lod4 = "lod4", Everywhere = "all", Billboard = "billboard";

    // One generated tree as the report describes it.
    public sealed record Tree(string Model, string Source, bool Vanilla, bool HasBillboard, int Lod4Kb, int Records);

    public static List<Tree> ReadReport(string output)
    {
        var path = Path.Combine(output, "TreeLODGen_report.csv");
        var trees = new List<Tree>();
        if (!File.Exists(path)) return trees;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(';');
            if (f.Length < 5 || f[4] != "generated") continue;
            string Field(int i) => i < f.Length ? f[i] : "";
            trees.Add(new Tree(f[0], f[2], Field(8) == "yes", Field(9) != "no",
                int.TryParse(Field(10), out var kb) ? kb : 0, int.TryParse(Field(11), out var records) ? records : 0));
        }
        return trees;
    }

    // What a tree gets: its own choice, else the default of the window's checkbox. A tree
    // TexGen cannot make a billboard for keeps its 3D models everywhere, or it would vanish.
    public static string Effective(Tree tree, bool far3d, IReadOnlyDictionary<string, string>? choices)
    {
        var choice = choices != null && choices.TryGetValue(tree.Model, out var c) ? c : far3d ? Everywhere : Lod4;
        return tree.HasBillboard ? choice : Everywhere;
    }

    // Rewrites the rule files of the output; returns how many trees use 3D at LOD 4 or more.
    public static int Write(string output, bool far3d, IReadOnlyDictionary<string, string>? choices)
    {
        var configDir = Path.Combine(output, "DynDOLOD");
        if (Directory.Exists(configDir))
            foreach (var stale in Directory.EnumerateFiles(configDir, "DynDOLOD_SSE_skyrimesm_*.ini")) File.Delete(stale);
        var trees = ReadReport(output);
        if (trees.Count == 0) return 0;

        Directory.CreateDirectory(configDir);
        var lines = new List<string> { "[Skyrim LODGen]" };
        var in3d = 0;
        foreach (var tree in trees)
        {
            var levels = Effective(tree, far3d, choices) switch
            {
                Everywhere => "Static LOD4,Static LOD8,Static LOD16",
                Billboard => "Billboard,Billboard,Billboard",
                _ => "Static LOD4,Billboard,Billboard",
            };
            if (!levels.StartsWith("Billboard")) in3d++;
            lines.Add($"LODGen{lines.Count}={tree.Model},{levels},Far LOD,Unchanged,17");
        }
        foreach (var preset in new[] { "high", "medium", "low" })
            File.WriteAllLines(Path.Combine(configDir, $"DynDOLOD_SSE_skyrimesm_{preset}.ini"), lines);
        return in3d;
    }

    // How many copies of each full model the last DynDOLOD run placed in object LOD, from
    // the lists DynDOLOD hands to LODGen. Empty when DynDOLOD has not run yet.
    public static Dictionary<string, int> PlacedCounts(string dyndolod)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var export = Path.Combine(dyndolod, "Edit Scripts", "Export");
        if (dyndolod == "" || !Directory.Exists(export)) return counts;
        foreach (var file in Directory.EnumerateFiles(export, "LODGen_*_Export_*.txt").Where(f => !f.Contains("TerrainUnderside", StringComparison.OrdinalIgnoreCase)))
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var start = line.IndexOf("\tMeshes\\", StringComparison.OrdinalIgnoreCase);
                    if (start < 0) continue;
                    var end = line.IndexOf('\t', start + 1);
                    if (end < 0) continue;
                    var model = line[(start + 8)..end];
                    counts[model] = counts.GetValueOrDefault(model) + 1;
                }
            }
            catch (IOException) { }
        return counts;
    }

    public static Dictionary<string, string>? LoadChoices(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
