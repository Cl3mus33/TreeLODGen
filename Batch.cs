using nifly;
using System.Globalization;

namespace TreeLODGen;

// Runs `split` over every tree of a mod that already ships hybrid LOD, and writes a CSV
// comparing each result with the author's files. The author's LOD is the ground truth
// that tells whether trunk/crown classification and crown reduction hold up.
static class Batch
{
    const string Suffix = "passthru_lod.nif";

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: TreeLODGen batch <modRoot> <outDir> [--sub <folder>] [--keep 0.4] [--area 0.8]");
            return 1;
        }
        var modRoot = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);
        var sub = "";
        var keep = 0.4f;
        var area = 0.8f;
        for (var i = 3; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--sub": sub = args[i + 1]; break;
                case "--keep": keep = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--area": area = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                default: Console.WriteLine($"Unknown option '{args[i]}'"); return 1;
            }
        }

        var lodDir = Path.Combine(modRoot, @"meshes\dyndolod\lod\trees");
        var trunkDir = Path.Combine(modRoot, @"dyndolod\render\billboards\dyndolod\lod\trees");
        var fullModels = Directory.EnumerateFiles(Path.Combine(modRoot, "meshes"), "*.nif", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\dyndolod\", StringComparison.OrdinalIgnoreCase))
            .ToLookup(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant());
        var authorTrunks = Directory.Exists(trunkDir)
            ? Directory.EnumerateFiles(trunkDir, "*_trunk.nif", SearchOption.AllDirectories).ToDictionary(p => Path.GetFileName(p).ToLowerInvariant())
            : new Dictionary<string, string>();

        Directory.CreateDirectory(outDir);
        using var csv = new StreamWriter(Path.Combine(outDir, "batch.csv"));
        csv.WriteLine("tree;status;crc_match;trunk_tris;author_trunk_tris;crown_tris_full;crown_tris_out;author_crown_tris;crown_area_out_vs_full;author_crown_area_vs_full;author_extra_shapes;notes;classification");

        int ok = 0, failed = 0;
        foreach (var authorLod in Directory.EnumerateFiles(lodDir, "*" + Suffix).OrderBy(p => p))
        {
            var stem = Path.GetFileName(authorLod)[..^Suffix.Length];
            var separator = stem.LastIndexOf('_');
            var name = stem[..separator].ToLowerInvariant();
            var authorCrc = stem[(separator + 1)..].ToUpperInvariant();

            var candidates = fullModels[name].ToList();
            if (candidates.Count == 0) { csv.WriteLine($"{name};no full model"); failed++; continue; }
            var fullPath = candidates.FirstOrDefault(p => NifUtil.Crc32(File.ReadAllBytes(p)).ToString("X8") == authorCrc) ?? candidates[0];

            var log = new List<string>();
            Split.Result result;
            try { result = Split.Process(fullPath, outDir, sub, new[] { (keep, area, 400) }, null, null, new[] { modRoot }, log.Add); }
            catch (Exception e) { csv.WriteLine($"{name};exception: {e.Message}"); failed++; continue; }
            if (result.Error != null) { csv.WriteLine($"{name};{result.Error};{result.Crc == authorCrc};;;;;;;;;{string.Join(",", result.Notes)};{result.Classification}"); failed++; continue; }

            var crownOut = Path.Combine(outDir, @"hybrid\input", sub, $"{name}_{result.Crc}{Suffix}");
            var (outTris, outArea, _) = Measure(crownOut, _ => true);
            var (fullCrownTris, fullCrownArea, _) = (result.CrownTrisFull, FullCrownArea(fullPath, result), 0);
            var (authorTris, authorArea, authorExtra) = Measure(authorLod, s => !s.Contains("FlatTrunk", StringComparison.OrdinalIgnoreCase));
            var authorTrunkTris = authorTrunks.TryGetValue($"{name}_{authorCrc}_trunk.nif".ToLowerInvariant(), out var authorTrunk) ? Measure(authorTrunk, _ => true).tris : -1;

            csv.WriteLine(string.Join(";", name, "ok", result.Crc == authorCrc, result.TrunkTris, authorTrunkTris, fullCrownTris, outTris, authorTris,
                (outArea / fullCrownArea).ToString("F2", CultureInfo.InvariantCulture), (authorArea / fullCrownArea).ToString("F2", CultureInfo.InvariantCulture),
                authorExtra, string.Join(",", result.Notes), result.Classification));
            ok++;
        }
        Console.WriteLine($"{ok} ok, {failed} failed -> {Path.Combine(outDir, "batch.csv")}");
        return 0;
    }

    static (int tris, float area, string shapes) Measure(string path, Func<string, bool> include)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) return (-1, 0, "");
        var tris = 0; var area = 0f; var names = new List<string>();
        foreach (var shape in nif.GetShapes())
        {
            if (!include(shape.name.get())) continue;
            names.Add($"{shape.name.get()}:{shape.GetNumTriangles()}");
            var verts = nif.GetVertsForShape(shape);
            var triangles = new vectorTriangle();
            shape.GetTriangles(triangles);
            tris += triangles.Count;
            foreach (var t in triangles) area += Islands.TriangleArea(verts[t.p1], verts[t.p2], verts[t.p3]);
        }
        return (tris, area, string.Join(",", names));
    }

    static float FullCrownArea(string fullPath, Split.Result result)
    {
        using var nif = new NifFile();
        nif.Load(fullPath);
        var crownNames = result.Classification.Split(' ').Where(c => c.Contains("=crown[")).Select(c => c[..c.IndexOf("=crown[")]).ToHashSet();
        var area = 0f;
        foreach (var shape in nif.GetShapes())
        {
            if (!crownNames.Contains(shape.name.get())) continue;
            var verts = nif.GetVertsForShape(shape);
            var triangles = new vectorTriangle();
            shape.GetTriangles(triangles);
            foreach (var t in triangles) area += Islands.TriangleArea(verts[t.p1], verts[t.p2], verts[t.p3]);
        }
        return area;
    }
}
