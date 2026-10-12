using nifly;

namespace TreeLODGen;

static class VColor
{
    // Diagnostic: average vertex colour and normal direction per shape.
    public static int Report(string path)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) { Console.WriteLine($"ERROR: failed to load {path}"); return 1; }
        foreach (var shape in nif.GetShapes())
        {
            var colors = new vectorColor4();
            nif.GetColorsForShape(shape, colors);
            var normals = nif.GetNormalsForShape(shape);
            var line = $"'{shape.name.get()}' verts={shape.GetNumVertices()}";
            if (colors.Count > 0) line += $" color avg=({colors.Average(c => c.r):F2},{colors.Average(c => c.g):F2},{colors.Average(c => c.b):F2},{colors.Average(c => c.a):F2}) min=({colors.Min(c => c.r):F2},{colors.Min(c => c.g):F2},{colors.Min(c => c.b):F2},{colors.Min(c => c.a):F2})";
            if (normals != null && normals.Count > 0) line += $" normal avg=({normals.Average(n => n.x):F2},{normals.Average(n => n.y):F2},{normals.Average(n => n.z):F2}) avgUp={normals.Average(n => n.z):F2}";
            Console.WriteLine(line);
        }
        return 0;
    }
}
