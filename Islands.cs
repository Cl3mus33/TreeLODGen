using nifly;

namespace TreeLODGen;

// A crown is a soup of disconnected leaf cards and twig clusters. An island is one
// connected piece of it, found after welding vertices that share a position (cards
// usually duplicate vertices along UV/normal seams).
sealed class Island
{
    public List<int> Triangles = new();
    public HashSet<ushort> Vertices = new();
    public float Area;
    public Vector3 Centroid = new();
}

static class Islands
{
    const float WeldGrid = 0.01f;

    public static List<Island> Find(vectorVector3 verts, vectorTriangle tris)
    {
        var parent = new int[verts.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Root(a); b = Root(b); if (a != b) parent[b] = a; }

        var byPosition = new Dictionary<(int, int, int), int>();
        for (var i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            var key = ((int)MathF.Round(v.x / WeldGrid), (int)MathF.Round(v.y / WeldGrid), (int)MathF.Round(v.z / WeldGrid));
            if (byPosition.TryGetValue(key, out var first)) Union(first, i);
            else byPosition[key] = i;
        }
        foreach (var t in tris) { Union(t.p1, t.p2); Union(t.p1, t.p3); }

        var islands = new Dictionary<int, Island>();
        for (var ti = 0; ti < tris.Count; ti++)
        {
            var t = tris[ti];
            var root = Root(t.p1);
            if (!islands.TryGetValue(root, out var island)) islands[root] = island = new Island();
            island.Triangles.Add(ti);
            island.Vertices.Add(t.p1); island.Vertices.Add(t.p2); island.Vertices.Add(t.p3);
            island.Area += TriangleArea(verts[t.p1], verts[t.p2], verts[t.p3]);
        }

        foreach (var island in islands.Values)
        {
            float x = 0, y = 0, z = 0;
            foreach (var vi in island.Vertices) { var v = verts[vi]; x += v.x; y += v.y; z += v.z; }
            var n = island.Vertices.Count;
            island.Centroid = new Vector3(x / n, y / n, z / n);
        }
        return islands.Values.ToList();
    }

    public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
        float vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
        float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    // Diagnostic: island statistics per shape, to judge how a crown is built before pruning it.
    public static int Report(string path)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) { Console.WriteLine($"ERROR: failed to load {path}"); return 1; }

        foreach (var shape in nif.GetShapes())
        {
            var verts = nif.GetVertsForShape(shape);
            var tris = new vectorTriangle();
            shape.GetTriangles(tris);
            var islands = Find(verts, tris);
            Console.WriteLine($"Shape '{shape.name.get()}': verts={verts.Count} tris={tris.Count} islands={islands.Count} area={islands.Sum(i => i.Area):F0}");

            foreach (var group in islands.GroupBy(i => Bucket(i.Triangles.Count)).OrderBy(g => g.Key))
                Console.WriteLine($"  {BucketLabel(group.Key),-10} islands={group.Count(),5}  tris={group.Sum(i => i.Triangles.Count),6}  area={group.Sum(i => i.Area),10:F0}  avgArea={group.Average(i => i.Area),8:F0}");
        }
        return 0;
    }

    static int Bucket(int tris) => tris <= 2 ? tris : tris <= 4 ? 4 : tris <= 8 ? 8 : tris <= 16 ? 16 : tris <= 64 ? 64 : 9999;
    static string BucketLabel(int bucket) => bucket switch { 1 => "1 tri", 2 => "2 tris", 4 => "3-4", 8 => "5-8", 16 => "9-16", 64 => "17-64", _ => "65+" };
}
