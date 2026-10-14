using Microsoft.Win32;
using System.Globalization;

namespace TreeLODGen;

// The tool's main job: read a load order, find every tree the game will show, and write
// a ready-to-install mod with 3D tree LOD models for DynDOLOD. Run it before TexGen and
// DynDOLOD.
static class Generate
{
    const string LodFolder = @"meshes\dyndolod\lod\trees";
    // Earlier versions kept their models in this subfolder. DynDOLOD looks into subfolders,
    // other generators (AnvilLOD) only read the tree LOD folder itself, so the models now go
    // there, and a list of them tells this tool which files are its own.
    const string OldSubfolder = "treelodgen";
    const string OwnList = "TreeLODGen_models.txt";
    // A tree counts as vanilla when its model path is the base game's or its record is one
    // of the base game's that a mod gave another model (Ulvenwald's oaks replace aspens).
    static readonly HashSet<string> BaseMasters = new(StringComparer.OrdinalIgnoreCase) { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
    const string StandInList = "TreeLODGen_standins.txt";

    public const string Usage = """
        TreeLODGen - 3D tree LOD models generated from the trees of your load order, in the
        format DynDOLOD defined. Run it before your LOD generation (TexGen and DynDOLOD, or
        another generator that reads that format), then install the output folder as a mod.

        Usage: TreeLODGen [options]

          --output <folder>     where to write the mod (default: "TreeLODGen Output" in the mods folder
                                of the Mod Organizer 2 instance in use, otherwise next to the tool)
          --data <folder>       the game's Data folder (default: found in the registry).
                                Start the tool through your mod manager so that Data shows your mods.
          --mo2 <folder>        read a Mod Organizer 2 instance directly instead (no need to start
                                the tool through MO2); combine with --profile <name> (default: Default)
          --plugins <file>      plugins.txt to use with --data
                                (default: %LOCALAPPDATA%\Skyrim Special Edition\plugins.txt)
          --all                 also generate for trees that already have a 3D LOD in the load order
          --levels <list>       per LOD level 4,8,16: leaf cards kept : leaf area restored : bark triangles
                                : leaf triangles a crown is never pruned below
                                (default: 0.4:0.8:400:2500,0.15:0.7:150:1000,0.06:0.6:60:400)
          --far-3d              use the 3D models at LOD 8 and 16 too, instead of billboards there;
                                several times heavier, a large forest can stop the game loading
          --tree-choices <file> JSON object of model path -> "lod4", "all" or "billboard", per tree
          --no-rules            do not write DynDOLOD rules for the generated trees
          --dyndolod <folder>   DynDOLOD folder; needed only for trees made of animated shapes alone,
                                so that TexGen can still render a billboard for them
          --no-pause            start at once and do not wait for a key at the end
        """;

    // Everything is first written to a staging folder and only moved into the output folder
    // once all trees are done, so that a cancelled or failed run leaves the output as it was.
    public static int Run(string[] args)
    {
        var stage = Path.Combine(Path.GetTempPath(), "TreeLODGen_stage_" + Guid.NewGuid().ToString("N"));
        try
        {
            return Run(args, stage);
        }
        catch (OperationCanceledException)
        {
            Ui.EndStep("cancelled");
            Console.WriteLine();
            Console.WriteLine("Cancelled. Nothing was changed in the output folder.");
            return 2;
        }
        finally
        {
            Ui.CancelRequested = false;
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static int Run(string[] args, string stage)
    {
        string output = "", data = "", mo2 = "", profile = "Default", pluginsTxt = "", dyndolod = "";
        // per LOD level: share of leaf cards kept, share of leaf area restored, bark triangle budget
        var levels = new[] { (0.4f, 0.8f, 400), (0.15f, 0.7f, 150), (0.06f, 0.6f, 60) };
        bool all = false, rules = true, far3d = false;
        Dictionary<string, string>? choices = null;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--output": output = Path.GetFullPath(args[++i]); break;
                    case "--data": data = args[++i]; break;
                    case "--mo2": mo2 = args[++i]; break;
                    case "--profile": profile = args[++i]; break;
                    case "--plugins": pluginsTxt = args[++i]; break;
                    case "--dyndolod": dyndolod = args[++i]; break;
                    case "--all": all = true; break;
                    case "--no-rules": rules = false; break;
                    case "--far-3d": far3d = true; break;
                    case "--tree-choices":
                        choices = TreeRules.LoadChoices(args[++i]);
                        if (choices == null) { Console.WriteLine($"Could not read the tree choices in {args[i]}."); return 1; }
                        break;
                    case "--levels":
                        var fields = args[++i].Split(',').Select(l => l.Split(':')).ToArray();
                        levels = fields.Select(l => (float.Parse(l[0], CultureInfo.InvariantCulture), float.Parse(l[1], CultureInfo.InvariantCulture), l.Length > 2 ? int.Parse(l[2]) : 400)).ToArray();
                        if (fields.All(l => l.Length > 3)) Split.CrownFloors = fields.Select(l => int.Parse(l[3])).ToArray();
                        break;
                    default: Console.WriteLine($"Unknown option '{args[i]}'\n\n{Usage}"); return 1;
                }
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or FormatException)
        {
            Console.WriteLine($"Could not read the options.\n\n{Usage}");
            return 1;
        }

        if (data == "") data = FindGameData() ?? "";
        if (data == "" || !File.Exists(Path.Combine(data, "Skyrim.esm")))
        {
            Console.WriteLine("Skyrim Special Edition's Data folder was not found. Point to it with --data <folder>.");
            return 1;
        }

        // Files read from archives are extracted for this run only. Nothing is kept between
        // runs: a copy from an earlier run could belong to an archive that has since been
        // updated, or that another mod now overrides.
        var cache = Path.Combine(stage, "cache");
        var legacyCache = Path.Combine(Path.GetTempPath(), "TreeLODGen_cache");
        try { if (Directory.Exists(legacyCache)) Directory.Delete(legacyCache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Vfs vfs;
        List<string> loadOrder;
        if (mo2 != "")
        {
            if (!File.Exists(Path.Combine(mo2, "profiles", profile, "modlist.txt")))
            {
                Console.WriteLine($"No profile '{profile}' in the Mod Organizer 2 instance {mo2}.");
                return 1;
            }
            // generated LOD and this tool's own earlier output must not count as existing LOD
            bool Generated(string mod) => mod.StartsWith("TreeLODGen", StringComparison.OrdinalIgnoreCase)
                || mod.Contains("DynDOLOD Output", StringComparison.OrdinalIgnoreCase) || mod.Contains("TexGen Output", StringComparison.OrdinalIgnoreCase)
                || mod.Contains("AnvilLOD Output", StringComparison.OrdinalIgnoreCase);
            if (!Announce($"Mod Organizer 2 profile '{profile}' of {mo2}", ref output, Path.Combine(mo2, "mods"), all, rules, far3d)) return 0;
            Ui.Step(1, 4, "Reading the load order");
            loadOrder = Plugins.ActiveLoadOrder(Path.Combine(mo2, "profiles", profile));
            Ui.EndStep($"{loadOrder.Count} active plugins");
            Ui.Step(2, 4, "Indexing mod files and archives");
            vfs = Vfs.FromMo2Profile(mo2, profile, data, cache, loadOrder, Generated, Indexing);
        }
        else
        {
            if (pluginsTxt == "") pluginsTxt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "plugins.txt");
            if (!Announce(data, ref output, Mo2.ModsFolderOfLauncher(), all, rules, far3d)) return 0;
            Ui.Step(1, 4, "Reading the load order");
            loadOrder = Plugins.ActiveLoadOrder(data, pluginsTxt);
            Ui.EndStep($"{loadOrder.Count} active plugins");
            Ui.Step(2, 4, "Indexing the Data folder and archives");
            vfs = Vfs.FromDataFolder(data, cache, loadOrder, Indexing);
        }
        loadOrder = loadOrder.Where(p => !p.StartsWith("DynDOLOD.", StringComparison.OrdinalIgnoreCase) && !p.Equals("Occlusion.esp", StringComparison.OrdinalIgnoreCase)).ToList();
        Textures.Resolver = vfs.Resolve;

        Ui.EndStep($"{vfs.LooseCount:N0} files, {vfs.ArchiveCount} archives");
        Ui.Step(3, 4, "Finding the trees");
        var trees = Plugins.WinningTrees(vfs, loadOrder, Console.WriteLine);
        var existingLod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, source) in vfs.FilesUnder(LodFolder))
            if (!path.Contains($@"\{OldSubfolder}\", StringComparison.OrdinalIgnoreCase)) existingLod.TryAdd(Path.GetFileName(path), source);
        // models written by an earlier run are not "existing LOD": they are about to be redone
        if (vfs.Resolve(Path.Combine(LodFolder, OwnList)) is { } installedList)
            foreach (var own in File.ReadAllLines(installedList)) existingLod.Remove(own.Trim());
        existingLod.Remove(OwnList);

        var lodDir = Path.Combine(output, LodFolder);
        var standInDir = dyndolod != "" ? Path.Combine(dyndolod, @"Edit Scripts\DynDOLOD\Render\Skyrim\Replace") : null;
        if (standInDir != null && !Directory.Exists(Path.Combine(dyndolod, "Edit Scripts", "DynDOLOD")))
        {
            Console.WriteLine($"{dyndolod} is not a DynDOLOD folder (no Edit Scripts\\DynDOLOD inside).");
            return 1;
        }
        var stageStandIns = standInDir != null ? Path.Combine(stage, "standins") : null;
        var stageLod = Path.Combine(stage, "lod");
        Directory.CreateDirectory(stageLod);
        var stageReport = Path.Combine(stage, "TreeLODGen_report.csv");
        var report = new StreamWriter(stageReport);
        report.WriteLine("model;editor_ids;mesh_source;crc;status;bark_tris;leaf_tris;notes;vanilla;billboard;lod4_kb;records");
        var counts = new SortedDictionary<string, int>();
        var generated = new List<string>();
        var foliage = new Dictionary<string, (int size, int tile)>();
        var skinnedOnly = new List<TreeRecord>();
        var forcedBillboards = new SortedDictionary<string, List<string>>();

        var groups = trees.GroupBy(t => t.Model.ToLowerInvariant()).OrderBy(g => g.Key).ToList();
        // the two conditions TexGen uses to decide that a base record is a tree worth a LOD
        bool Candidate(IGrouping<string, TreeRecord> group) => group.Any(t => t.IsLodSized()) && group.Key.Contains("tree");
        var candidates = groups.Count(Candidate);
        Ui.EndStep($"{trees.Count} tree records, {candidates} trees large enough for LOD");
        Ui.Step(4, 4, "Generating LOD models");
        var done = 0;

        using (report)
        foreach (var group in groups)
        {
            Ui.ThrowIfCancelled();
            if (Candidate(group)) Ui.Tree(done++, candidates, Path.GetFileNameWithoutExtension(group.Key));
            var relative = @"meshes\" + group.Key;
            string status, crc = "", source = vfs.SourceOf(relative) ?? "", detail = ";;", extra = ";;;";

            if (!group.Any(t => t.IsLodSized())) status = "too small for tree LOD";
            else if (!group.Key.Contains("tree")) status = "not a tree for TexGen (no 'tree' in the model path)";
            else if (vfs.Resolve(relative) is not { } fullPath) status = "mesh not found";
            else
            {
                crc = NifUtil.Crc32(File.ReadAllBytes(fullPath)).ToString("X8");
                var lodName = $"{Path.GetFileNameWithoutExtension(group.Key)}_{crc}passthru_lod.nif";
                if (!all && existingLod.TryGetValue(lodName, out var lodSource)) status = $"has 3D LOD ({lodSource})";
                else
                {
                    Split.Result result;
                    try { result = Split.Process(fullPath, output, "", levels, null, null, Array.Empty<string>(), _ => { }, group.Key, stageLod, stageStandIns, false); }
                    catch (Exception e) { result = new Split.Result { Error = "failed: " + e.Message }; }
                    status = result.Error ?? "generated";
                    detail = $"{result.TrunkTris};{result.CrownTrisFull};{string.Join(",", result.Notes)}";
                    var lod4 = Path.Combine(stageLod, $"{Path.GetFileNameWithoutExtension(group.Key)}_{crc}passthru_lod.nif");
                    var billboard = !result.SkinnedOnly || standInDir != null;
                    extra = $"{(vfs.InBaseGame(relative) || group.Any(t => BaseMasters.Contains(t.Owner)) ? "yes" : "no")};{(billboard ? "yes" : "no")};{(File.Exists(lod4) ? new FileInfo(lod4).Length / 1024 : 0)};{group.Count()}";
                    if (result.Error == null)
                    {
                        foreach (var d in result.Density)
                            foliage[d.texture] = (d.size, d.tile);
                        generated.Add(group.Key);
                        Ui.TreeDone(Path.GetFileNameWithoutExtension(group.Key), Path.Combine(stageLod, $"{Path.GetFileNameWithoutExtension(group.Key)}_{crc}passthru_lod.nif"));
                    }
                    if (result.SkinnedOnly)
                    {
                        skinnedOnly.AddRange(group);
                        foreach (var tree in group)
                        {
                            if (!forcedBillboards.TryGetValue(tree.Owner, out var lines)) forcedBillboards[tree.Owner] = lines = new List<string>();
                            lines.Add($"{tree.Owner};{tree.LocalId:X8};{tree.EditorId},True");
                        }
                    }
                }
            }

            var bucket = status.StartsWith("has 3D LOD") ? "already have a 3D LOD (left alone, use --all to redo them)" : status;
            counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
            report.WriteLine($"{group.Key};{string.Join(",", group.Select(t => t.EditorId))};{source};{crc};{status};{detail};{extra}");
        }

        // all trees are done: replace the previous models with the new ones
        Ui.ThrowIfCancelled();
        Directory.CreateDirectory(lodDir);
        var listPath = Path.Combine(lodDir, OwnList);
        if (File.Exists(listPath))
            foreach (var stale in File.ReadAllLines(listPath).Select(l => Path.Combine(lodDir, l.Trim())).Where(File.Exists)) File.Delete(stale);
        var oldDir = Path.Combine(lodDir, OldSubfolder);
        if (Directory.Exists(oldDir))
        {
            foreach (var stale in Directory.EnumerateFiles(oldDir, "*passthru_lod*.nif")) File.Delete(stale);
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
        }
        var written = new List<string>();
        foreach (var model in Directory.EnumerateFiles(stageLod, "*.nif"))
        {
            File.Move(model, Path.Combine(lodDir, Path.GetFileName(model)), true);
            written.Add(Path.GetFileName(model));
        }
        File.WriteAllLines(listPath, written);
        File.Move(stageReport, Path.Combine(output, "TreeLODGen_report.csv"), true);

        var configDir = Path.Combine(output, "DynDOLOD");
        if (Directory.Exists(configDir)) foreach (var stale in Directory.EnumerateFiles(configDir, "DynDOLOD_SSE_*")) File.Delete(stale);
        // One rule per generated tree, from the per-tree choices when there are any: the 3D
        // model at LOD 4 and billboards beyond by default (DynDOLOD's own high preset), or 3D
        // at every level on request. The far levels cover most of the map, so 3D there
        // multiplies what the game has to load.
        if (rules) TreeRules.Write(output, far3d, choices);
        if (standInDir != null)
        {
            // The stand-ins live in DynDOLOD's own folder. Those of the previous run are
            // removed first, so that a tree mod that is gone leaves nothing behind there.
            var standInList = Path.Combine(standInDir, StandInList);
            if (File.Exists(standInList))
                foreach (var stale in File.ReadAllLines(standInList).Select(l => Path.Combine(standInDir, l.Trim())).Where(File.Exists)) File.Delete(stale);
            var standIns = new List<string>();
            if (Directory.Exists(stageStandIns))
                foreach (var file in Directory.EnumerateFiles(stageStandIns!, "*.nif", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(stageStandIns!, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(standInDir, relative))!);
                    File.Move(file, Path.Combine(standInDir, relative), true);
                    standIns.Add(relative);
                }
            Directory.CreateDirectory(standInDir);
            File.WriteAllLines(standInList, standIns);
            Directory.CreateDirectory(configDir);
            foreach (var (owner, lines) in forcedBillboards)
                File.WriteAllLines(Path.Combine(configDir, $"DynDOLOD_SSE_TexGen_Billboards_{owner.Replace(".", "").ToLowerInvariant()}.txt"), lines);
        }

        Ui.Tree(candidates, candidates, "");
        Ui.EndStep($"{generated.Count} trees, {generated.Count * levels.Length} models");
        Console.WriteLine();
        foreach (var (status, count) in counts) Console.WriteLine($"  {count,4}  {status}");
        Console.WriteLine();
        Ui.Color(ConsoleColor.Green, () => Console.WriteLine($"Done in {Ui.Elapsed}: {generated.Count} trees, {generated.Count * levels.Length} LOD models written to"));
        Console.WriteLine($"  {output}");
        Console.WriteLine($"Details for every tree: {Path.Combine(output, "TreeLODGen_report.csv")}");
        if (skinnedOnly.Count > 0 && standInDir == null && !rules)
            Console.WriteLine($"NOTE: {skinnedOnly.Select(t => t.Model).Distinct().Count()} trees are made of animated shapes only. TexGen cannot make a billboard for them; without --dyndolod they have no LOD past level 4.");
        Console.WriteLine();
        Console.WriteLine("Next: enable that folder as a mod, then run your LOD generation.");
        Console.WriteLine("With DynDOLOD: TexGen first, then DynDOLOD in advanced mode with Ultra ticked.");
        TileSizeAdvice(foliage);
        return 0;
    }

    // DynDOLOD shrinks every full texture a 3D LOD uses to its "Max tile size full". A mod
    // that keeps all its branches on one large sheet loses them entirely when that sheet
    // becomes a 256 pixel tile: the needles fall under the alpha threshold and only trunks
    // remain. The advice is the smallest tile on which every foliage texture still shows.
    static void TileSizeAdvice(Dictionary<string, (int size, int tile)> foliage)
    {
        if (foliage.Count == 0) return;
        var (texture, worst) = foliage.MaxBy(f => f.Value.tile);
        if (worst.tile <= 256) return;
        Console.WriteLine();
        Ui.Color(ConsoleColor.Yellow, () => Console.WriteLine($"DynDOLOD 'Max tile size full': set it to {worst.tile} or more."));
        Console.WriteLine($"  {Path.GetFileName(texture)} ({worst.size} pixels) holds foliage too fine for a smaller tile:");
        Console.WriteLine("  below that the leaves fade out and the trees show as bare trunks in the distance.");
    }
    // Says what is about to happen and where the result goes, then waits for the user's go
    // when the tool has a window of its own. Without --output, the result goes into the
    // mods folder of the Mod Organizer 2 instance in use when there is one, so that it shows
    // up there as a mod; otherwise next to the tool.
    public static (string folder, string note) DefaultOutput(string? modsFolder)
    {
        var useMods = modsFolder != null && Directory.Exists(modsFolder);
        return (Path.Combine(useMods ? modsFolder! : AppContext.BaseDirectory, "TreeLODGen Output"),
            useMods ? "a mod in your Mod Organizer 2 mods folder" : "next to the tool, to install as a mod");
    }

    static bool Announce(string source, ref string output, string? modsFolder, bool all, bool rules, bool far3d)
    {
        var where = Ui.OnStep != null ? "the folder chosen in the window" : "set with --output";
        if (output == "") (output, where) = DefaultOutput(modsFolder);
        Console.WriteLine($"Reads   {source}");
        Console.WriteLine($"Writes  {output}");
        Console.WriteLine($"        ({where})");
        Console.WriteLine($"Trees   {(all ? "every tree, including those that already have a 3D LOD" : "trees that have no 3D LOD yet")}");
        Console.WriteLine($"Rules   {(!rules ? "none written (--no-rules)" : far3d ? "3D models at LOD 4, 8 and 16 for the generated trees" : "3D models at LOD 4, billboards beyond, for the generated trees")}");
        Console.WriteLine();
        if (!Ui.Interactive) return true;

        Ui.Color(ConsoleColor.Yellow, () => Ui.Screen.Write("Press Enter to start, Esc to cancel. "));
        while (true)
        {
            var key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.Enter) break;
            if (key == ConsoleKey.Escape) { Ui.Screen.WriteLine(); Console.WriteLine("Cancelled, nothing was written."); return false; }
        }
        Ui.Screen.WriteLine();
        Ui.Screen.WriteLine();
        Ui.ResetClock();
        return true;
    }

    static void Indexing(int files)
    {
        Ui.ThrowIfCancelled();
        Ui.Progress($"{files:N0} files");
    }

    public static string? FindGameData()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition" })
        {
            using var registry = Registry.LocalMachine.OpenSubKey(key);
            if (registry?.GetValue("Installed Path") is string path && Directory.Exists(Path.Combine(path, "Data"))) return Path.Combine(path, "Data");
        }
        return null;
    }
}
