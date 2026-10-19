using nifly;

namespace TreeLODGen;

// Stage 1: split a full tree model into the two inputs DynDOLOD needs for a hybrid
// 3D tree LOD model -- a trunk NIF (rendered to a billboard by TexGen) and a crown
// NIF (merged with that billboard by LODGen in HYBRID mode).
static class Split
{
    static readonly string[] DefaultCrownHints = { "leaf", "leaves", "crown", "frond", "needle", "foliage" };
    static readonly string[] DefaultTrunkHints = { "trunk", "bark" };

    const string TrunkRelDir = @"DynDOLOD\Render\Billboards\DynDOLOD\lod\trees";
    const string CrownRelDir = @"hybrid\input";
    public static string LevelInputDir(int level) => level == 0 ? CrownRelDir : $@"hybrid\input_{level}";
    public const string Lod3dRelDir = "lod3d";
    const string ReplaceRelDir = @"DynDOLOD\Render\Replace";
    const string HybridOutRelDir = @"meshes\DynDOLOD\lod\trees";

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: TreeLODGen split <full.nif> <outDir> [--sub <folder>] [--data root1;root2] [--keep 0.4] [--area 0.8] [--crown-hints a,b] [--trunk-hints a,b]");
            return 1;
        }

        var fullPath = args[1];
        var outDir = Path.GetFullPath(args[2]);
        var sub = "";
        var crownHints = DefaultCrownHints;
        var trunkHints = DefaultTrunkHints;
        var keep = 1f;
        var area = 0.8f;
        var dataRoots = Array.Empty<string>();
        string? meshPath = null;
        for (var i = 3; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--sub": sub = args[i + 1]; break;
                case "--mesh-path": meshPath = args[i + 1]; break;
                case "--data": dataRoots = args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries); break;
                case "--keep": keep = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--area": area = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--crown-hints": crownHints = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--trunk-hints": trunkHints = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                default: Console.WriteLine($"Unknown option '{args[i]}'"); return 1;
            }
        }

        var result = Process(fullPath, outDir, sub, new[] { (keep, area, 400) }, crownHints, trunkHints, dataRoots, Console.WriteLine, meshPath);
        return result.Error == null ? 0 : 1;
    }

    public sealed class Result
    {
        public string? Error;
        public string Crc = "";
        public List<string> Notes = new();
        public int TrunkShapes, CrownShapes, TrunkTris, CrownTrisFull, CrownTrisOut;
        public float CrownAreaFull, CrownAreaOut;
        public string Classification = "";
        public List<(string texture, int size, int tile)> Density = new();
        // true when the tree only exists as animated shapes, which TexGen cannot render
        public bool SkinnedOnly;
    }

    sealed record Part(NiShape Shape, HashSet<ushort>? Vertices, int Tris, bool IsCrown);

    public static Result Process(string fullPath, string outDir, string sub, (float keep, float area, int trunkTris)[] levels, string[]? crownHints, string[]? trunkHints, string[] dataRoots, Action<string> log, string? meshRelativePath = null, string? lodDir = null, string? standInDir = null, bool hybridInputs = true, string? crcName = null)
    {
        crownHints ??= DefaultCrownHints;
        trunkHints ??= DefaultTrunkHints;
        var result = new Result();

        using var src = new NifFile();
        if (src.Load(fullPath) != 0)
        {
            result.Error = $"failed to load {fullPath}";
            log("ERROR: " + result.Error);
            return result;
        }

        // DynDOLOD pairs a LOD model with its full model through the CRC32 of the full model file.
        var crc = result.Crc = crcName ?? NifUtil.Crc32(File.ReadAllBytes(fullPath)).ToString("X8");
        var baseName = $"{Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant()}_{crc}";
        log($"Full model: {Path.GetFileName(fullPath)}  CRC32={crc}");

        var trunkParts = new List<Part>();
        var crownParts = new List<Part>();
        var classification = new List<string>();
        foreach (var shape in SourceShapes(src))
        {
            var tris = (int)shape.GetNumTriangles();
            var name = shape.name.get();

            var hinted = HintedKind(name, crownHints, trunkHints, out var reason);
            if (hinted == null && NifUtil.AlphaOf(src, shape) == null) { hinted = false; reason = "opaque shape"; }
            if (hinted != null)
            {
                (hinted.Value ? crownParts : trunkParts).Add(new Part(shape, null, tris, hinted.Value));
                classification.Add($"{name}={(hinted.Value ? "crown" : "trunk")}[{reason}]");
                log($"  '{name}' tris={tris} -> {(hinted.Value ? "crown" : "trunk")} ({reason})");
                continue;
            }

            // An alpha tested shape with a neutral name can hold bark and foliage together.
            var wood = WoodSplit.Analyze(src, shape, dataRoots);
            if (wood.WoodTris > 0) trunkParts.Add(new Part(shape, wood.IsMixed ? wood.WoodVertices : null, wood.WoodTris, false));
            if (wood.LeafTris > 0) crownParts.Add(new Part(shape, wood.IsMixed ? wood.LeafVertices : null, wood.LeafTris, true));
            var kind = wood.IsMixed ? "mixed" : wood.WoodTris > 0 ? "trunk" : "crown";
            classification.Add($"{name}={kind}[{wood.Method}]");
            log($"  '{name}' tris={tris} -> {kind} ({wood.Method}): wood {wood.WoodTris} tris in {wood.WoodIslands} pieces, foliage {wood.LeafTris} tris in {wood.LeafIslands} pieces");
            foreach (var ambiguous in wood.Ambiguous.Take(5))
            {
                result.Notes.Add($"ambiguous-piece:{name}");
                log($"  WARNING: '{name}' has a piece that is neither clearly bark nor foliage ({ambiguous}) -- treated as foliage.");
            }
        }
        result.Classification = string.Join(" ", classification);
        result.TrunkShapes = trunkParts.Count;
        result.CrownShapes = crownParts.Count;
        result.TrunkTris = trunkParts.Sum(p => p.Tris);
        result.CrownTrisFull = crownParts.Sum(p => p.Tris);

        foreach (var part in crownParts)
            if (WoodSplit.FoliageTile(src, part.Shape) is { } tile)
                result.Density.Add((tile.texture.ToLowerInvariant(), tile.textureSize, tile.tile));

        if (crownParts.Count == 0 && trunkParts.Count == 0)
        {
            result.Error = "no geometry found (nothing to make a LOD model from)";
            log("ERROR: " + result.Error);
            return result;
        }

        // A crown made of few, large cards has nothing to spare: pruning it by the same
        // share as a dense one leaves a handful of oversized planes. Below a number of
        // triangles per level the crown is kept as it is, and above it never pruned under it.
        if (result.CrownTrisFull > 0)
            levels = levels.Select((l, i) => (MathF.Max(l.keep, MathF.Min(1f, (float)CrownFloors[Math.Min(i, CrownFloors.Length - 1)] / result.CrownTrisFull)), l.area, l.trunkTris)).ToArray();

        // A dead tree is bark alone: its LOD is the reduced bark, with the triangles a crown
        // would have cost spent on the branches that make its silhouette.
        var barkOnly = crownParts.Count == 0;
        if (barkOnly)
        {
            result.Notes.Add("bare tree: bark only LOD");
            log("NOTE: no foliage -- bare tree, the LOD is made from the bark alone.");
            levels = levels.Select(l => (l.keep, l.area, l.trunkTris * BareTreeBudget)).ToArray();
            hybridInputs = false;
        }

        // DynDOLOD takes one model per LOD level (passthru_lod.nif, then _1, _2), each lighter
        // than the last. Every level goes to its own LODGen input folder under the same name,
        // because the hybrid step pairs a crown with its trunk billboard by that name.
        for (var level = 0; level < levels.Length; level++)
        {
            log($"Level {level}:");
            if (hybridInputs)
            {
                var crownPath = Path.Combine(outDir, LevelInputDir(level), sub, baseName + "passthru_lod.nif");
                WriteStaticNif(src, crownParts, crownPath, levels[level].keep, levels[level].area, result, log);
                log($"Crown: {crownPath}");
            }

            // Full 3D variant: the pruned crown plus the bark reduced to a triangle budget,
            // ready to use as the LOD model, with no billboard trunk and no LODGen step.
            var lodPath = Path.Combine(lodDir ?? Path.Combine(outDir, Lod3dRelDir, sub), baseName + "passthru_lod" + (level == 0 ? "" : $"_{level}") + ".nif");
            WriteStaticNif(src, crownParts.Concat(trunkParts).ToList(), lodPath, levels[level].keep, levels[level].area, result, log, levels[level].trunkTris);
            log($"3D LOD: {lodPath}");
        }
        foreach (var part in crownParts) ReportUvRange(src, part, result, log);

        if (!hybridInputs) { }
        else if (trunkParts.Count > 0)
        {
            var trunkPath = Path.Combine(outDir, TrunkRelDir, sub, baseName + "_trunk.nif");
            WriteStaticNif(src, trunkParts, trunkPath, 1f, 1f, result, log);
            log($"Trunk: {trunkPath}");
        }
        else
        {
            result.Notes.Add("no trunk shape");
            log("WARNING: no trunk found -- LODGen will fall back to a vanilla trunk billboard if one matches.");
        }

        // TexGen cannot render a tree that only exists as skinned geometry, so it makes no
        // billboard and the tree vanishes beyond the first LOD level. It accepts a static
        // stand-in named <tree>_static.nif in its Replace folder.
        result.SkinnedOnly = trunkParts.Concat(crownParts).Any(p => p.Shape.IsSkinned());
        if (meshRelativePath != null && result.SkinnedOnly && (hybridInputs || standInDir != null))
        {
            var staticPath = Path.Combine(standInDir ?? Path.Combine(outDir, ReplaceRelDir), Path.GetDirectoryName(meshRelativePath)!, Path.GetFileNameWithoutExtension(meshRelativePath) + "_static.nif");
            WriteStaticNif(src, trunkParts.Concat(crownParts).ToList(), staticPath, 1f, 1f, result, _ => { });
            result.Notes.Add("static render stand-in written");
            log($"Static stand-in for TexGen: {staticPath}");
        }

        if (hybridInputs) Directory.CreateDirectory(Path.Combine(outDir, HybridOutRelDir));
        return result;
    }

    // Vanilla style trees hold the same geometry twice, skinned for wind and static for the
    // far state. The static copy is the one to use; a tree whose static copy is empty or
    // partial (wind only mods) falls back to its skinned shapes.
    static List<NiShape> SourceShapes(NifFile nif)
    {
        var shapes = nif.GetShapes().Where(s => s.GetNumTriangles() > 0).ToList();
        var skinned = shapes.Where(s => s.IsSkinned()).ToList();
        var fixedShapes = shapes.Where(s => !s.IsSkinned()).ToList();
        if (skinned.Count == 0 || fixedShapes.Count == 0) return shapes;
        long skinnedTris = skinned.Sum(s => (long)s.GetNumTriangles()), fixedTris = fixedShapes.Sum(s => (long)s.GetNumTriangles());
        return fixedTris * 2 >= skinnedTris ? fixedShapes : skinned;
    }

    // Shape names only decide when they are explicit, since many mods use generic names.
    static bool? HintedKind(string shapeName, string[] crownHints, string[] trunkHints, out string reason)
    {
        var name = shapeName.ToLowerInvariant();
        var crownHint = crownHints.FirstOrDefault(h => name.Contains(h));
        if (crownHint != null) { reason = $"name contains '{crownHint}'"; return true; }
        var trunkHint = trunkHints.FirstOrDefault(h => name.Contains(h));
        if (trunkHint != null) { reason = $"name contains '{trunkHint}'"; return false; }
        reason = "";
        return null;
    }

    // LOD inputs must be plain static geometry: a fresh BSFadeNode root with only the
    // wanted geometry, so collision, controllers, bones and switch nodes never come along.
    const int BareTreeBudget = 2;
    const float TilingTolerance = 0.05f;
    public static int[] CrownFloors = { 2500, 1000, 400 };
    const string CrownSuffix = " Crown";
    const string TrunkSuffix = " Trunk";

    // DynDOLOD pairs each LOD shape with a shape of the full model through their names, to
    // carry over texture swaps. A LOD name that contains the name of another full model
    // shape can get paired with that one: a trunk taken for the foliage shape has the whole
    // foliage texture replaced, and every tree using it loses its crown. So the name is the
    // first of these that contains no other shape's name: the source name with its kind,
    // then a name of its own with the kind, then one without it. The last is for mods whose
    // full models have a shape plainly called "Trunk" or "Crown" that is something else.
    static string LodShapeName(NifFile src, List<NiShape> sourceShapes, NiShape shape, bool isCrown, int index)
    {
        var term = isCrown ? "Crown" : "Trunk";
        var own = shape.name.get();
        var texture = src.GetTexturePathByIndex(shape, 0);
        bool Mistaken(string name) => sourceShapes.Any(other =>
        {
            var otherName = other.name.get();
            return otherName.Length > 0 && !otherName.Equals(own, StringComparison.OrdinalIgnoreCase)
                && name.Contains(otherName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(src.GetTexturePathByIndex(other, 0), texture, StringComparison.OrdinalIgnoreCase);
        });
        var candidates = new[]
        {
            own.Contains(term, StringComparison.OrdinalIgnoreCase) ? own : own + (isCrown ? CrownSuffix : TrunkSuffix),
            $"{term} {index}",
            $"{(isCrown ? "Leaves" : "Bark")} {index}",
        };
        var chosen = candidates.FirstOrDefault(c => !Mistaken(c)) ?? $"LOD shape {index}";
        // Bark that repeats its texture along the trunk cannot live on DynDOLOD's atlas: a
        // tile there is one copy of the texture, and coordinates past it read whatever lies
        // next to it, which shows as dark and grey bands. "noatlas" makes that shape use its
        // own texture, where repeating works.
        if (!isCrown)
        {
            var uvs = src.GetUvsForShape(shape);
            if (uvs.Any(uv => uv.u < -TilingTolerance || uv.u > 1 + TilingTolerance || uv.v < -TilingTolerance || uv.v > 1 + TilingTolerance)) chosen += " noatlas";
        }
        return chosen;
    }
    static void WriteStaticNif(NifFile src, List<Part> parts, string path, float keep, float area, Result result, Action<string> log, int trunkTris = 0)
    {
        using var dst = new NifFile();
        dst.CreateNamedBSFadeNode(src.GetHeader().GetVersion(), "Scene Root");

        var barkNames = new HashSet<string>();
        var sourceShapes = src.GetShapes().ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var shape = part.Shape;
            // In a full 3D LOD the two kinds of shape are told apart by name, which is also
            // what DynDOLOD reads to apply its crown and trunk brightness settings.
            var cloneName = trunkTris > 0 ? LodShapeName(src, sourceShapes, shape, part.IsCrown, index) : shape.name.get();
            if (trunkTris > 0 && !part.IsCrown) barkNames.Add(cloneName);
            var clone = dst.CloneShape(shape, cloneName, src);
            if (clone.GetBlockName() != "BSTriShape")
            {
                clone = ToBSTriShape(dst, clone, src, shape);
                if (!result.Notes.Contains("rebuilt as BSTriShape")) result.Notes.Add("rebuilt as BSTriShape");
            }

            // The clone hangs directly off the new root, so it has to carry the transforms
            // of every node that sat above the source shape. A skinned shape is placed by
            // its skin instead of its parents.
            MatTransform global;
            if (shape.IsSkinned())
            {
                var globalToSkin = new MatTransform();
                src.GetShapeTransformGlobalToSkin(shape, globalToSkin);
                global = globalToSkin.InverseTransform();

                dst.DeleteSkinning(clone);
                var shader = NifUtil.ShaderOf(dst, clone);
                if (shader != null) shader.shaderFlags1 &= ~NifUtil.SLSF1_Skinned;
                if (!result.Notes.Contains($"skinned:{shape.name.get()}")) result.Notes.Add($"skinned:{shape.name.get()}");
                log($"  NOTE: '{shape.name.get()}' was skinned -- skin removed, vertices kept in bind pose.");
            }
            else
            {
                global = NifUtil.GlobalTransform(src, shape);
            }
            clone.transform = global;
            if (!NifUtil.IsIdentity(global))
            {
                if (!result.Notes.Contains($"transformed:{shape.name.get()}")) result.Notes.Add($"transformed:{shape.name.get()}");
                log($"  NOTE: '{shape.name.get()}' is not at the origin of its file -- its placement was baked into the LOD vertices.");
            }

            if (part.Vertices != null)
            {
                var others = new vectoruint16(Enumerable.Range(0, shape.GetNumVertices()).Select(i => (ushort)i).Where(i => !part.Vertices.Contains(i)).ToList());
                dst.DeleteVertsForShape(clone, others);
                clone.UpdateBounds();
            }

        }

        var barkShapes = dst.GetShapes().Where(s => barkNames.Contains(s.name.get())).ToList();
        var barkTotal = barkShapes.Sum(s => (long)s.GetNumTriangles());
        foreach (var shape in dst.GetShapes())
        {
            if (barkShapes.Any(b => b.name.get() == shape.name.get()))
            {
                // each bark shape gets its share of the budget
                Simplify.Apply(dst, shape, (int)Math.Max(12, trunkTris * shape.GetNumTriangles() / Math.Max(1, barkTotal)), log);
            }
            else if (keep < 1f)
            {
                Prune.Apply(dst, shape, keep, area, log);
                ClampSmallUvOvershoot(dst, shape);
            }
            NifUtil.BakeTransform(dst, shape);
            if (NifUtil.PlainLodShader(dst, shape) is { } removed && !result.Notes.Contains($"shader:{removed}"))
            {
                result.Notes.Add($"shader:{removed}");
                log($"  NOTE: '{shape.name.get()}' had a {removed} shader -- the LOD keeps its diffuse and normal map only.");
            }
        }

        // cloning a shape of a vanilla style tree drags parent nodes along; the shapes hang
        // off the new root, so none of those nodes is needed
        dst.DeleteUnreferencedNodes();
        foreach (var type in new[] { "BSMultiBoundNode", "BSMultiBound", "BSMultiBoundOBB", "NiSwitchNode", "BSTreeNode", "BSLeafAnimNode", "NiNode" })
            dst.GetHeader().DeleteBlockByType(type, type != "BSMultiBoundNode");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (dst.Save(path) != 0)
            throw new IOException($"failed to save {path}");
    }

    // Many vanilla trees keep their static geometry in the older NiTriShape / BSLODTriShape
    // blocks. LODGen's hybrid step turns those into files DynDOLOD then fails to read, so
    // the geometry is rebuilt as a BSTriShape that keeps the original shader and alpha.
    static NiShape ToBSTriShape(NifFile nif, NiShape old, NifFile source, NiShape sourceShape)
    {
        const uint none = 0xFFFFFFFF;
        var name = old.name.get();
        // geometry is read from the loaded source file: a fresh clone's data is not linked yet
        var verts = source.GetVertsForShape(sourceShape);
        var uvs = source.GetUvsForShape(sourceShape);
        var normals = source.GetNormalsForShape(sourceShape);
        var tris = new vectorTriangle();
        sourceShape.GetTriangles(tris);
        var colors = new vectorColor4();
        var hasColors = sourceShape.HasVertexColors() && source.GetColorsForShape(sourceShape, colors) && colors.Count == verts.Count;
        var transform = old.transform;
        var shader = nif.GetShader(old);
        var alpha = old.HasAlphaProperty() ? nif.GetAlphaProperty(old) : null;

        // detach what is kept, then drop the old geometry
        old.SetShaderPropertyRef(none);
        old.SetAlphaPropertyRef(none);
        nif.DeleteShape(old);

        var shape = normals != null && normals.Count == verts.Count
            ? nif.CreateShapeFromData(name, verts, tris, uvs, normals)
            : nif.CreateShapeFromData(name, verts, tris, uvs);
        if (shape.HasShaderProperty()) nif.DeleteShader(shape);
        if (shader != null) shape.SetShaderPropertyRef(nif.GetBlockID(shader));
        if (alpha != null) shape.SetAlphaPropertyRef(nif.GetBlockID(alpha));
        if (hasColors)
        {
            shape.SetVertexColors(true);
            nif.SetColorsForShape(shape, colors);
        }
        shape.transform = transform;
        nif.CalcTangentsForShape(shape);
        shape.UpdateBounds();
        return shape;
    }

    // Cards whose UVs poke a few percent past the texture edge would make LODGen cut every
    // such triangle in two; pulling them back onto the edge changes nothing visible.
    static void ClampSmallUvOvershoot(NifFile nif, NiShape shape)
    {
        const float maxOvershoot = 0.05f;
        var uvs = nif.GetUvsForShape(shape);
        if (uvs.Count == 0) return;
        if (uvs.Any(uv => uv.u < -maxOvershoot || uv.u > 1 + maxOvershoot || uv.v < -maxOvershoot || uv.v > 1 + maxOvershoot)) return;
        if (uvs.All(uv => uv.u >= 0 && uv.u <= 1 && uv.v >= 0 && uv.v <= 1)) return;
        var clamped = new vectorVector2(uvs.Select(uv => new Vector2(Math.Clamp(uv.u, 0f, 1f), Math.Clamp(uv.v, 0f, 1f))).ToList());
        nif.SetUvsForShape(shape, clamped);
    }

    static void ReportUvRange(NifFile nif, Part part, Result result, Action<string> log)
    {
        var all = nif.GetUvsForShape(part.Shape);
        if (all.Count == 0) return;
        var uvs = part.Vertices == null ? all.ToList() : part.Vertices.Select(i => all[i]).ToList();
        const float tolerance = 0.05f;
        if (uvs.All(uv => uv.u >= -tolerance && uv.u <= 1 + tolerance && uv.v >= -tolerance && uv.v <= 1 + tolerance)) return;
        result.Notes.Add($"uv-out-of-range:{part.Shape.name.get()}");
        log($"  WARNING: foliage of '{part.Shape.name.get()}' has UVs outside 0..1 (u[{uvs.Min(v => v.u):F2},{uvs.Max(v => v.u):F2}] v[{uvs.Min(v => v.v):F2},{uvs.Max(v => v.v):F2}]) -- LODGen will split them and add triangles.");
    }
}
