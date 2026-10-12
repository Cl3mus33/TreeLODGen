using System.Diagnostics;

namespace TreeLODGen;

// Runs LODGen in HYBRID mode: merges the crown NIFs written by `split` with the trunk
// billboards TexGen rendered from the trunk NIFs.
static class Hybrid
{
    const string DefaultDynDOLOD = @"C:\Modding\DynDOLOD";

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: TreeLODGen hybrid <outDir> <TexGen_Output> [--dyndolod <DynDOLOD folder>] [--template template4.nif]");
            return 1;
        }

        var outDir = Path.GetFullPath(args[1]);
        var texGenOutput = Path.GetFullPath(args[2]);
        var dyndolod = DefaultDynDOLOD;
        var template = "template4.nif";
        for (var i = 3; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--dyndolod": dyndolod = args[i + 1]; break;
                case "--template": template = args[i + 1]; break;
                default: Console.WriteLine($"Unknown option '{args[i]}'"); return 1;
            }
        }

        var lodGen = Path.Combine(dyndolod, @"Edit Scripts\LODGenx64.exe");
        var flatNif = Path.Combine(dyndolod, @"docs\trees.ultra\tools\hybrids", template);
        var pathData = Path.Combine(texGenOutput, @"Textures\DynDOLOD\LOD\Trees");
        var finalOutput = Path.Combine(outDir, @"meshes\DynDOLOD\lod\trees");
        foreach (var required in new[] { lodGen, flatNif })
            if (!File.Exists(required)) { Console.WriteLine($"ERROR: not found: {required}"); return 1; }
        if (!Directory.Exists(pathData)) { Console.WriteLine($"ERROR: not found: {pathData}"); return 1; }

        var missing = 0;
        for (var level = 0; Directory.Exists(Path.Combine(outDir, Split.LevelInputDir(level))); level++)
        {
            var pathInput = Path.Combine(outDir, Split.LevelInputDir(level));
            // LODGen silently skips a crown whose trunk billboard is missing, so check up front.
            foreach (var crown in Directory.EnumerateFiles(pathInput, "*passthru_lod.nif", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pathInput, crown);
                var trunkTxt = Path.Combine(pathData, rel[..^"passthru_lod.nif".Length] + "_trunk.txt");
                if (File.Exists(trunkTxt)) continue;
                if (level == 0) Console.WriteLine($"WARNING: no trunk billboard for {rel} (expected {trunkTxt}) -- run TexGen with the trunk NIF installed.");
                missing++;
            }

            // Every level is built under the plain name, then renamed to the suffix DynDOLOD
            // expects for that level (_1, _2); level 0 keeps the plain name.
            var levelOutput = level == 0 ? finalOutput : Path.Combine(outDir, $@"hybrid\output_{level}");
            Directory.CreateDirectory(levelOutput);
            var configPath = Path.Combine(outDir, $@"hybrid\hybrid_{level}.txt");
            File.WriteAllLines(configPath, new[]
            {
                "GameMode=HYBRID",
                $"FlatNif={flatNif}",
                $@"PathData={pathData}\",
                $@"PathInput={pathInput}\",
                $@"PathOutput={levelOutput}\",
            });

            var startInfo = new ProcessStartInfo(lodGen) { UseShellExecute = false };
            startInfo.ArgumentList.Add(configPath);
            using var process = Process.Start(startInfo)!;
            process.WaitForExit();
            if (process.ExitCode != 0) return process.ExitCode;

            if (level == 0) continue;
            foreach (var built in Directory.EnumerateFiles(levelOutput, "*passthru_lod.nif", SearchOption.AllDirectories))
            {
                var target = Path.Combine(finalOutput, Path.GetRelativePath(levelOutput, built))[..^".nif".Length] + $"_{level}.nif";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(built, target, true);
            }
        }
        return missing > 0 ? 2 : 0;
    }
}
