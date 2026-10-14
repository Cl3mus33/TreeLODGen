using System.Text.Json;

namespace TreeLODGen;

// Per-tree exceptions to DynDOLOD's own "tree" rule. By default no rule is written: the
// generated models carry the names that rule looks for, so the LOD levels of every tree are
// set in DynDOLOD itself. A rule is only written for a tree given its own choice in
// "Choose trees", and for a tree TexGen cannot make a billboard for, which keeps its 3D
// models at every level or it would vanish past LOD 4. Changing choices only rewrites the
// rule file: the trees are not generated again (DynDOLOD has to run again, though).
static class TreeRules
{
    public const string Default = "default", Lod4 = "lod4", Everywhere = "all", Billboard = "billboard";

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

    // What a tree gets: its own choice, else DynDOLOD's tree rule.
    public static string Effective(Tree tree, IReadOnlyDictionary<string, string>? choices)
    {
        if (!tree.HasBillboard) return Everywhere;
        return choices != null && choices.TryGetValue(tree.Model, out var c) && c != "" ? c : Default;
    }

    // Rewrites the rule files of the output; returns how many trees got a rule of their own.
    public static int Write(string output, IReadOnlyDictionary<string, string>? choices)
    {
        var configDir = Path.Combine(output, "DynDOLOD");
        if (Directory.Exists(configDir))
            foreach (var stale in Directory.EnumerateFiles(configDir, "DynDOLOD_SSE_skyrimesm_*.ini")) File.Delete(stale);
        var trees = ReadReport(output);
        if (trees.Count == 0) return 0;

        var lines = new List<string> { "[Skyrim LODGen]" };
        var own = 0;
        foreach (var tree in trees)
        {
            var choice = Effective(tree, choices);
            if (choice == Default) continue;
            own++;
            var levels = choice switch
            {
                Everywhere => "Static LOD4,Static LOD8,Static LOD16",
                Billboard => "Billboard,Billboard,Billboard",
                _ => "Static LOD4,Billboard,Billboard",
            };
            lines.Add($"LODGen{lines.Count}={tree.Model},{levels},Far LOD,Unchanged,17");
        }
        if (own == 0) return 0;
        Directory.CreateDirectory(configDir);
        foreach (var preset in new[] { "high", "medium", "low" })
            File.WriteAllLines(Path.Combine(configDir, $"DynDOLOD_SSE_skyrimesm_{preset}.ini"), lines);
        return own;
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
