using System.Text.Json;

namespace TreeLODGen;

// PGPatcher (ParallaxGen) rewrites meshes and records, for each, the CRC32 of the mesh
// before and after in ParallaxGen_Diff.json. DynDOLOD reads that file and looks LOD models
// up by the ORIGINAL CRC, so that LOD shipped for a mod keeps matching after patching. The
// LOD models made here must therefore be named with the original CRC too, or DynDOLOD
// does not find them and falls back to billboards.
static class ParallaxGenDiff
{
    public static Dictionary<string, (uint original, uint patched)> Load(Vfs vfs)
    {
        var map = new Dictionary<string, (uint, uint)>(StringComparer.OrdinalIgnoreCase);
        if (vfs.Resolve("ParallaxGen_Diff.json") is not { } path) return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (entry.Value.TryGetProperty("crc32original", out var original) && entry.Value.TryGetProperty("crc32patched", out var patched))
                    map[entry.Name] = (original.GetUInt32(), patched.GetUInt32());
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException)
        {
            Console.WriteLine($"NOTE: could not read {path}: {e.Message}");
        }
        return map;
    }

    // The CRC DynDOLOD will look the LOD up by: the original one when this exact mesh is the
    // patched version PGPatcher recorded, otherwise the mesh's own.
    public static uint LodCrc(Dictionary<string, (uint original, uint patched)> map, string relativePath, uint actual) =>
        map.TryGetValue(relativePath, out var entry) && entry.patched == actual ? entry.original : actual;
}
