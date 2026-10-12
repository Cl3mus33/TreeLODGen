using nifly;

namespace TreeLODGen;

// Stage 3: reduces bark geometry (trunk and branches) to a triangle budget so it can stay
// in the LOD as real 3D instead of a flat billboard. Edge collapse driven by quadric error:
// a vertex is merged into a neighbour where that moves the surface least. Collapses only
// ever land on an existing vertex, so UVs, normals and colours stay valid as they are.
static class Simplify
{
    const float WeldGrid = 0.01f;
    const double BoundaryWeight = 10.0;

    public static void Apply(NifFile nif, NiShape shape, int targetTris, Action<string> log)
    {
        var verts = nif.GetVertsForShape(shape);
        var uvs = nif.GetUvsForShape(shape);
        var source = new vectorTriangle();
        shape.GetTriangles(source);
        if (source.Count <= targetTris || uvs.Count != verts.Count) return;

        // Vertices doubled along UV seams share a position; topology is taken on the welded
        // points so that a collapse cannot tear the bark open along a seam.
        var point = new int[verts.Count];
        var positions = new List<Vector3>();
        var byPosition = new Dictionary<(int, int, int), int>();
        for (var i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            var key = ((int)MathF.Round(v.x / WeldGrid), (int)MathF.Round(v.y / WeldGrid), (int)MathF.Round(v.z / WeldGrid));
            if (!byPosition.TryGetValue(key, out var id)) { byPosition[key] = id = positions.Count; positions.Add(v); }
            point[i] = id;
        }

        var faces = new List<int[]>();      // three points
        var corners = new List<ushort[]>(); // three original vertices, carrying the attributes
        foreach (var t in source)
        {
            int a = point[t.p1], b = point[t.p2], c = point[t.p3];
            if (a == b || b == c || a == c) continue;
            faces.Add(new[] { a, b, c });
            corners.Add(new[] { t.p1, t.p2, t.p3 });
        }

        var alive = Enumerable.Repeat(true, faces.Count).ToArray();
        var facesOf = new HashSet<int>[positions.Count];
        for (var i = 0; i < facesOf.Length; i++) facesOf[i] = new HashSet<int>();
        for (var f = 0; f < faces.Count; f++) foreach (var p in faces[f]) facesOf[p].Add(f);

        var quadric = new double[positions.Count][];
        for (var i = 0; i < quadric.Length; i++) quadric[i] = new double[10];
        var edgeUse = new Dictionary<(int, int), int>();
        for (var f = 0; f < faces.Count; f++)
        {
            var (n, area) = Normal(positions[faces[f][0]], positions[faces[f][1]], positions[faces[f][2]]);
            if (area <= 0) continue;
            AddPlane(quadric, faces[f], n, positions[faces[f][0]], area);
            for (var k = 0; k < 3; k++)
            {
                var e = Ordered(faces[f][k], faces[f][(k + 1) % 3]);
                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
            }
        }
        // An open edge (end of a branch, base of the trunk) is held in place by a plane
        // standing on it, otherwise the opening would shrink away first.
        for (var f = 0; f < faces.Count; f++)
        {
            var (n, area) = Normal(positions[faces[f][0]], positions[faces[f][1]], positions[faces[f][2]]);
            if (area <= 0) continue;
            for (var k = 0; k < 3; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % 3];
                if (edgeUse[Ordered(a, b)] != 1) continue;
                var edge = Sub(positions[b], positions[a]);
                var wall = Cross(edge, n);
                var length = Math.Sqrt(Dot(wall, wall));
                if (length <= 0) continue;
                AddPlane(quadric, new[] { a, b }, Scale(wall, 1 / length), positions[a], area * BoundaryWeight);
            }
        }

        var version = new int[positions.Count];
        var queue = new PriorityQueue<(int from, int to, int vFrom, int vTo), double>();
        void Offer(int from)
        {
            foreach (var to in facesOf[from].SelectMany(f => faces[f]).Where(p => p != from).Distinct())
                queue.Enqueue((from, to, version[from], version[to]), Cost(quadric[from], quadric[to], positions[to]));
        }
        for (var p = 0; p < positions.Count; p++) Offer(p);

        var remaining = alive.Count(a => a);
        while (remaining > targetTris && queue.TryDequeue(out var candidate, out _))
        {
            var (from, to, vFrom, vTo) = candidate;
            if (version[from] != vFrom || version[to] != vTo || facesOf[from].Count == 0) continue;

            // refuse a collapse that would fold a neighbouring triangle over
            var folds = false;
            foreach (var f in facesOf[from])
            {
                if (faces[f].Contains(to)) continue;
                var before = Normal(positions[faces[f][0]], positions[faces[f][1]], positions[faces[f][2]]).n;
                var moved = faces[f].Select(p => p == from ? positions[to] : positions[p]).ToArray();
                var (after, area) = Normal(moved[0], moved[1], moved[2]);
                if (area <= 0 || Dot(before, after) < 0.2) { folds = true; break; }
            }
            if (folds) continue;

            // attribute vertices currently in use at the target, to hand over the corners to
            var targets = facesOf[to].Where(f => alive[f]).Select(f => corners[f][Array.IndexOf(faces[f], to)]).Distinct().ToList();
            foreach (var f in facesOf[from].ToList())
            {
                if (faces[f].Contains(to))
                {
                    alive[f] = false; remaining--;
                    foreach (var p in faces[f]) facesOf[p].Remove(f);
                    continue;
                }
                var k = Array.IndexOf(faces[f], from);
                var uv = uvs[corners[f][k]];
                // the copy of the target vertex lying on the same side of a UV seam
                corners[f][k] = targets.MinBy(a => (uvs[a].u - uv.u) * (uvs[a].u - uv.u) + (uvs[a].v - uv.v) * (uvs[a].v - uv.v));
                faces[f][k] = to;
                facesOf[to].Add(f);
            }
            facesOf[from].Clear();
            for (var i = 0; i < 10; i++) quadric[to][i] += quadric[from][i];
            version[from]++; version[to]++;
            Offer(to);
            foreach (var neighbour in facesOf[to].SelectMany(f => faces[f]).Where(p => p != to).Distinct()) Offer(neighbour);
        }

        var result = new vectorTriangle();
        var used = new HashSet<ushort>();
        for (var f = 0; f < faces.Count; f++)
        {
            if (!alive[f]) continue;
            result.Add(new Triangle(corners[f][0], corners[f][1], corners[f][2]));
            foreach (var c in corners[f]) used.Add(c);
        }
        shape.SetTriangles(result);
        var unused = new vectoruint16(Enumerable.Range(0, verts.Count).Select(i => (ushort)i).Where(i => !used.Contains(i)).ToList());
        if (unused.Count > 0) nif.DeleteVertsForShape(shape, unused);
        shape.UpdateBounds();
        log($"  simplified '{shape.name.get()}': tris {source.Count} -> {shape.GetNumTriangles()}");
    }

    static (int, int) Ordered(int a, int b) => a < b ? (a, b) : (b, a);
    static double[] Sub(Vector3 a, Vector3 b) => new double[] { a.x - b.x, a.y - b.y, a.z - b.z };
    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };

    static (double[] n, double area) Normal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Cross(Sub(b, a), Sub(c, a));
        var length = Math.Sqrt(Dot(n, n));
        return length <= 1e-12 ? (n, 0) : (Scale(n, 1 / length), length / 2);
    }

    // Adds the squared-distance-to-plane form of one plane to each listed point.
    static void AddPlane(double[][] quadric, int[] points, double[] n, Vector3 on, double weight)
    {
        var d = -(n[0] * on.x + n[1] * on.y + n[2] * on.z);
        var q = new[] { n[0] * n[0], n[0] * n[1], n[0] * n[2], n[0] * d, n[1] * n[1], n[1] * n[2], n[1] * d, n[2] * n[2], n[2] * d, d * d };
        foreach (var p in points) for (var i = 0; i < 10; i++) quadric[p][i] += q[i] * weight;
    }

    static double Cost(double[] a, double[] b, Vector3 at)
    {
        double x = at.x, y = at.y, z = at.z;
        double Q(int i) => a[i] + b[i];
        return Q(0) * x * x + 2 * Q(1) * x * y + 2 * Q(2) * x * z + 2 * Q(3) * x + Q(4) * y * y + 2 * Q(5) * y * z + 2 * Q(6) * y + Q(7) * z * z + 2 * Q(8) * z + Q(9);
    }
}
