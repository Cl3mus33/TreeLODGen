using nifly;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace TreeLODGen;

// Diagnostic: unlit orthographic front view with alpha testing, to compare a pruned
// crown against the full one by eye and by covered area. Not a LOD preview of lighting.
static class Render
{
    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: TreeLODGen render <file.nif> <out.png> <dataRoot[;dataRoot2]> [--upp 3] [--view front|side]");
            return 1;
        }
        var roots = args[3].Split(';', StringSplitOptions.RemoveEmptyEntries);
        var unitsPerPixel = 3f;
        var side = false;
        for (var i = 4; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--upp") unitsPerPixel = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--view") side = args[i + 1] == "side";
        }

        using var nif = new NifFile();
        if (nif.Load(args[1]) != 0) { Console.WriteLine($"ERROR: failed to load {args[1]}"); return 1; }

        // Fixed frame so renders of different files of the same tree line up pixel for pixel.
        float halfWidth = 450f, minZ = -60f, maxZ = 1680f;
        for (var i = 4; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--half") halfWidth = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--top") maxZ = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--bottom") minZ = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        }
        var width = (int)(2 * halfWidth / unitsPerPixel);
        var height = (int)((maxZ - minZ) / unitsPerPixel);
        var color = new int[width * height];
        var depth = new float[width * height];
        Array.Fill(depth, float.MaxValue);
        var crownPixels = new bool[width * height];

        var tempDir = Path.Combine(Path.GetTempPath(), "TreeLODGen_render");
        Directory.CreateDirectory(tempDir);

        foreach (var shape in nif.GetShapes())
        {
            var texture = Textures.Load(nif.GetTexturePathByIndex(shape, 0), roots);
            if (texture == null) { Console.WriteLine($"  texture not found for '{shape.name.get()}': {nif.GetTexturePathByIndex(shape, 0)}"); continue; }
            var alpha = NifUtil.AlphaOf(nif, shape);
            var threshold = alpha != null ? alpha.threshold : 0;
            var isTrunk = shape.name.get().Contains("trunk", StringComparison.OrdinalIgnoreCase);

            var verts = nif.GetVertsForShape(shape);
            var uvs = nif.GetUvsForShape(shape);
            var tris = new vectorTriangle();
            shape.GetTriangles(tris);
            foreach (var t in tris)
            {
                var p = new[] { t.p1, t.p2, t.p3 };
                var sx = new float[3]; var sy = new float[3]; var sd = new float[3];
                for (var k = 0; k < 3; k++)
                {
                    var v = verts[p[k]];
                    sx[k] = ((side ? v.y : v.x) + halfWidth) / unitsPerPixel;
                    sy[k] = (maxZ - v.z) / unitsPerPixel;
                    sd[k] = side ? -v.x : v.y;
                }
                var area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
                if (MathF.Abs(area) < 1e-6f) continue;

                int x0 = Math.Max(0, (int)MathF.Floor(sx.Min())), x1 = Math.Min(width - 1, (int)MathF.Ceiling(sx.Max()));
                int y0 = Math.Max(0, (int)MathF.Floor(sy.Min())), y1 = Math.Min(height - 1, (int)MathF.Ceiling(sy.Max()));
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    var w0 = ((sx[1] - px) * (sy[2] - py) - (sx[2] - px) * (sy[1] - py)) / area;
                    var w1 = ((sx[2] - px) * (sy[0] - py) - (sx[0] - px) * (sy[2] - py)) / area;
                    var w2 = 1f - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                    var u = w0 * uvs[p[0]].u + w1 * uvs[p[1]].u + w2 * uvs[p[2]].u;
                    var v = w0 * uvs[p[0]].v + w1 * uvs[p[1]].v + w2 * uvs[p[2]].v;
                    u -= MathF.Floor(u); v -= MathF.Floor(v);
                    var texel = texture.Texel(u, v);
                    if (((texel >> 24) & 0xFF) < threshold) continue;

                    var d = w0 * sd[0] + w1 * sd[1] + w2 * sd[2];
                    var index = y * width + x;
                    if (!isTrunk) crownPixels[index] = true;
                    if (d >= depth[index]) continue;
                    depth[index] = d;
                    color[index] = texel | unchecked((int)0xFF000000);
                }
            }
        }

        for (var i = 0; i < color.Length; i++) if (depth[i] == float.MaxValue) color[i] = unchecked((int)0xFF9FB4C8);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(color, 0, data.Scan0, color.Length);
        bitmap.UnlockBits(data);
        bitmap.Save(args[2], ImageFormat.Png);

        var covered = depth.Count(d => d != float.MaxValue);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {width}x{height}, covered={covered} px ({(float)covered / color.Length:P1}), crown={crownPixels.Count(c => c)} px -> {args[2]}");
        return 0;
    }
}
