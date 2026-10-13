using nifly;

namespace TreeLODGen;

// Many tree mods merge bark and foliage into a single shape on one atlas texture, so
// trunk and crown cannot be told apart by shape. Wood is then found per connected piece:
// bark samples the atlas where it is fully opaque, a leaf card always covers cut-out texels.
static class WoodSplit
{
    const float OpaqueIslandRatio = 0.9f;
    const int FallbackWoodIslandTris = 200;

    public sealed class Analysis
    {
        public HashSet<ushort> WoodVertices = new();
        public HashSet<ushort> LeafVertices = new();
        public int WoodTris, LeafTris, WoodIslands, LeafIslands;
        public float WoodArea, LeafArea;
        public string Method = "";
        public List<string> Ambiguous = new();
        public bool IsMixed => WoodTris > 0 && LeafTris > 0;
    }

    public static Analysis Analyze(NifFile nif, NiShape shape, string[] dataRoots)
    {
        var analysis = new Analysis();
        var verts = nif.GetVertsForShape(shape);
        var uvs = nif.GetUvsForShape(shape);
        var tris = new vectorTriangle();
        shape.GetTriangles(tris);
        if (tris.Count == 0 || uvs.Count != verts.Count) return analysis;

        var texture = Textures.Load(nif.GetTexturePathByIndex(shape, 0), dataRoots);
        var alpha = NifUtil.AlphaOf(nif, shape);
        var threshold = alpha != null ? alpha.threshold : 128;
        analysis.Method = texture != null ? "texture alpha" : "uv tiling (texture not found)";

        // Index connectivity only: welding by position would glue leaf cards to the branch they touch.
        var parent = new int[verts.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        foreach (var t in tris) { parent[Root(t.p2)] = Root(t.p1); parent[Root(t.p3)] = Root(t.p1); }

        var islands = new Dictionary<int, (List<Triangle> tris, float area, float opaque)>();
        foreach (var t in tris)
        {
            var area = Islands.TriangleArea(verts[t.p1], verts[t.p2], verts[t.p3]);
            var opaque = texture != null
                ? IsOpaque(texture, uvs[t.p1], uvs[t.p2], uvs[t.p3], threshold)
                : IsTiling(uvs[t.p1]) || IsTiling(uvs[t.p2]) || IsTiling(uvs[t.p3]);
            var root = Root(t.p1);
            if (!islands.TryGetValue(root, out var island)) island = (new List<Triangle>(), 0f, 0f);
            island.tris.Add(t);
            islands[root] = (island.tris, island.area + area, island.opaque + (opaque ? area : 0f));
        }

        foreach (var island in islands.Values)
        {
            var ratio = island.area > 0 ? island.opaque / island.area : 0f;
            var isWood = texture != null ? ratio >= OpaqueIslandRatio : ratio > 0.5f || island.tris.Count > FallbackWoodIslandTris;
            if (texture != null && ratio > 0.3f && ratio < OpaqueIslandRatio && island.tris.Count > 50)
                analysis.Ambiguous.Add($"{island.tris.Count} tris {ratio:P0} opaque");

            var target = isWood ? analysis.WoodVertices : analysis.LeafVertices;
            foreach (var t in island.tris) { target.Add(t.p1); target.Add(t.p2); target.Add(t.p3); }
            if (isWood) { analysis.WoodTris += island.tris.Count; analysis.WoodArea += island.area; analysis.WoodIslands++; }
            else { analysis.LeafTris += island.tris.Count; analysis.LeafArea += island.area; analysis.LeafIslands++; }
        }
        // Each Triangle read above is a view into the native list: without this the list can
        // be collected once the loop that filled the islands is over, while the islands still
        // read their triangles from it, and the read hits freed memory.
        GC.KeepAlive(tris);
        return analysis;
    }

    static bool IsTiling(Vector2 uv) => uv.u < -0.01f || uv.u > 1.01f || uv.v < -0.01f || uv.v > 1.01f;

    // A triangle is opaque when every sample of a barycentric grid lands on an opaque texel.
    static bool IsOpaque(Textures.Texture texture, Vector2 a, Vector2 b, Vector2 c, int threshold)
    {
        const int steps = 6;
        for (var i = 0; i <= steps; i++)
        for (var j = 0; j <= steps - i; j++)
        {
            // pulled slightly inwards so samples do not fall on the neighbouring atlas cell
            var w0 = (i + 0.25f) / (steps + 0.75f);
            var w1 = (j + 0.25f) / (steps + 0.75f);
            var w2 = 1f - w0 - w1;
            if (w2 < 0) continue;
            if (texture.Alpha(w0 * a.u + w1 * b.u + w2 * c.u, w0 * a.v + w1 * b.v + w2 * c.v) < threshold) return false;
        }
        return true;
    }

    // The smallest atlas tile on which the foliage texture of a shape still shows. Shrinking
    // averages alpha, so thin needles and twigs drop under the alpha test long before broad
    // leaves do; a sheet that holds every branch of a mod can lose all of them at 256 pixels.
    const float KeptCoverage = 0.7f;

    public static (int tile, int textureSize, string texture)? FoliageTile(NifFile nif, NiShape shape)
    {
        var path = nif.GetTexturePathByIndex(shape, 0);
        var texture = Textures.Load(path, Array.Empty<string>());
        if (texture == null) return null;
        var threshold = NifUtil.AlphaOf(nif, shape) is { } alpha ? alpha.threshold : 128;
        var size = Math.Max(texture.Width, texture.Height);
        var full = texture.Coverage(Math.Max(texture.StoredWidth, texture.StoredHeight), threshold);
        if (full <= 0) return null;
        var tile = 256;
        while (tile < size && texture.Coverage(tile, threshold) < full * KeptCoverage) tile *= 2;
        return (Math.Min(tile, size), size, path);
    }

    // World units covered by one texel of the full texture, over the foliage of a shape.
    // Comparing it with TexGen's units per pixel tells which tile size keeps crowns as
    // sharp as the billboards around them.
    public static (float unitsPerTexel, int textureSize, string texture)? FoliageDensity(NifFile nif, NiShape shape, HashSet<ushort>? vertices)
    {
        var path = nif.GetTexturePathByIndex(shape, 0);
        var texture = Textures.Load(path, Array.Empty<string>());
        if (texture == null) return null;
        var verts = nif.GetVertsForShape(shape);
        var uvs = nif.GetUvsForShape(shape);
        var tris = new vectorTriangle();
        shape.GetTriangles(tris);
        double world = 0, uv = 0;
        foreach (var t in tris)
        {
            if (vertices != null && !vertices.Contains(t.p1)) continue;
            world += Islands.TriangleArea(verts[t.p1], verts[t.p2], verts[t.p3]);
            var a = uvs[t.p1]; var b = uvs[t.p2]; var c = uvs[t.p3];
            uv += 0.5 * Math.Abs((b.u - a.u) * (c.v - a.v) - (c.u - a.u) * (b.v - a.v));
        }
        if (uv <= 0) return null;
        var size = Math.Max(texture.Width, texture.Height);
        return ((float)Math.Sqrt(world / (uv * texture.Width * texture.Height)), size, path);
    }

    // Diagnostic: how a shape would be divided.
    public static int Report(string path, string[] dataRoots)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) { Console.WriteLine($"ERROR: failed to load {path}"); return 1; }
        foreach (var shape in nif.GetShapes())
        {
            if (shape.GetNumTriangles() == 0) continue;
            var a = Analyze(nif, shape, dataRoots);
            Console.WriteLine($"{Path.GetFileName(path)} '{shape.name.get()}' [{a.Method}]: wood {a.WoodTris} tris / {a.WoodIslands} islands / area {a.WoodArea:F0}   leaves {a.LeafTris} tris / {a.LeafIslands} islands / area {a.LeafArea:F0}");
            foreach (var note in a.Ambiguous.Take(5)) Console.WriteLine($"    ambiguous island: {note}");
        }
        return 0;
    }
}
