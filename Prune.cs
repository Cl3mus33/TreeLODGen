using nifly;

namespace TreeLODGen;

// Stage 2: stochastic pruning of a crown. Dropping whole leaf cards and enlarging the
// survivors keeps the silhouette and density that edge-collapse decimation destroys
// on alpha tested foliage.
static class Prune
{
    // keep = fraction of islands kept; area = fraction of the original leaf area the
    // kept islands are scaled back up to.
    public static void Apply(NifFile nif, NiShape shape, float keep, float area, Action<string> log)
    {
        var verts = nif.GetVertsForShape(shape);
        var tris = new vectorTriangle();
        shape.GetTriangles(tris);
        var islands = Islands.Find(verts, tris);
        if (islands.Count < 2) return;

        // Walking the islands along a Morton curve and keeping one every 1/keep spreads
        // the survivors evenly through the crown, with no random clumps or holes.
        float minX = verts.Min(v => v.x), minY = verts.Min(v => v.y), minZ = verts.Min(v => v.z);
        var size = MathF.Max(verts.Max(v => v.x) - minX, MathF.Max(verts.Max(v => v.y) - minY, verts.Max(v => v.z) - minZ));
        var ordered = islands.OrderBy(i => Morton(i.Centroid, minX, minY, minZ, size)).ToList();
        var kept = new List<Island>();
        var dropped = new List<Island>();
        var accumulator = 0.5f;
        foreach (var island in ordered)
        {
            accumulator += keep;
            if (accumulator >= 1f) { accumulator -= 1f; kept.Add(island); }
            else dropped.Add(island);
        }
        if (kept.Count == 0) return;

        var totalArea = islands.Sum(i => i.Area);
        var keptArea = kept.Sum(i => i.Area);
        var scale = MathF.Sqrt(MathF.Max(1f, totalArea * area / keptArea));

        var profile = new Profile(verts);
        foreach (var island in kept)
        {
            var c = island.Centroid;
            foreach (var vi in island.Vertices)
            {
                var v = verts[vi];
                verts[vi] = profile.Clamp(new Vector3(c.x + (v.x - c.x) * scale, c.y + (v.y - c.y) * scale, c.z + (v.z - c.z) * scale));
            }
        }
        nif.SetVertsForShape(shape, verts);

        var toDelete = new vectoruint16(dropped.SelectMany(i => i.Vertices).OrderBy(v => v).ToList());
        nif.DeleteVertsForShape(shape, toDelete);
        shape.UpdateBounds();

        log($"  pruned '{shape.name.get()}': islands {islands.Count} -> {kept.Count}, tris {tris.Count} -> {shape.GetNumTriangles()}, card scale x{scale:F2}, leaf area {keptArea * scale * scale / totalArea:P0} of original");
        GC.KeepAlive(tris); // the islands hold views into this native list
    }

    // Radial outline of the crown by height. Enlarged cards are pulled back inside it, so
    // long branch cards (conifers) cannot stick out past the silhouette of the full tree.
    sealed class Profile
    {
        const int Bands = 24;
        readonly float _centerX, _centerY, _minZ, _maxZ;
        readonly float[] _radius = new float[Bands];

        public Profile(vectorVector3 verts)
        {
            _centerX = verts.Average(v => v.x);
            _centerY = verts.Average(v => v.y);
            _minZ = verts.Min(v => v.z);
            _maxZ = verts.Max(v => v.z);
            foreach (var v in verts)
            {
                var band = Band(v.z);
                _radius[band] = MathF.Max(_radius[band], Radius(v));
            }
            // a band's limit also covers its neighbours, the outline is not that precise
            var smoothed = new float[Bands];
            for (var i = 0; i < Bands; i++)
                smoothed[i] = MathF.Max(_radius[i], MathF.Max(_radius[Math.Max(0, i - 1)], _radius[Math.Min(Bands - 1, i + 1)]));
            _radius = smoothed;
        }

        int Band(float z) => Math.Clamp((int)((z - _minZ) / MathF.Max(1e-3f, _maxZ - _minZ) * Bands), 0, Bands - 1);
        float Radius(Vector3 v) => MathF.Sqrt((v.x - _centerX) * (v.x - _centerX) + (v.y - _centerY) * (v.y - _centerY));

        public Vector3 Clamp(Vector3 v)
        {
            var z = Math.Clamp(v.z, _minZ, _maxZ);
            var limit = _radius[Band(z)];
            var radius = Radius(v);
            if (radius <= limit || radius < 1e-3f) return new Vector3(v.x, v.y, z);
            var factor = limit / radius;
            return new Vector3(_centerX + (v.x - _centerX) * factor, _centerY + (v.y - _centerY) * factor, z);
        }
    }

    static ulong Morton(Vector3 p, float minX, float minY, float minZ, float size)
    {
        ulong Spread(float value)
        {
            var q = (ulong)Math.Clamp((int)(value / size * 1023f), 0, 1023);
            ulong result = 0;
            for (var bit = 0; bit < 10; bit++) result |= ((q >> bit) & 1UL) << (bit * 3);
            return result;
        }
        return Spread(p.x - minX) | (Spread(p.y - minY) << 1) | (Spread(p.z - minZ) << 2);
    }
}
