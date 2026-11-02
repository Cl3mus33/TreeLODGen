using nifly;

namespace TreeLODGen;

static class Inspect
{
    public static int Run(string path)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0)
        {
            Console.WriteLine($"ERROR: failed to load {path}");
            return 1;
        }

        var bytes = File.ReadAllBytes(path);
        Console.WriteLine($"{Path.GetFileName(path)}  {bytes.Length} bytes  CRC32={NifUtil.Crc32(bytes):X8}");

        var header = nif.GetHeader();
        var typeCounts = new SortedDictionary<string, int>();
        for (uint i = 0; i < header.GetNumBlocks(); i++)
        {
            var type = header.GetBlockById(i).GetBlockName();
            typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
        }
        Console.WriteLine("Blocks: " + string.Join(", ", typeCounts.Select(kv => $"{kv.Key}x{kv.Value}")));
        var root = nif.GetRootNode();
        Console.WriteLine($"Root: {root.GetBlockName()} '{root.name.get()}'");

        foreach (var shape in nif.GetShapes())
        {
            Console.WriteLine($"\nShape [{nif.GetBlockID(shape)}] {shape.GetBlockName()} '{shape.name.get()}'");
            Console.WriteLine($"  path: {NifUtil.NodePath(nif, shape)}");
            Console.WriteLine($"  verts={shape.GetNumVertices()} tris={shape.GetNumTriangles()} skinned={shape.IsSkinned()} vcolors={shape.HasVertexColors()}");
            var normals = nif.GetNormalsForShape(shape); var zero = normals == null ? -1 : normals.Count(n => n.x * n.x + n.y * n.y + n.z * n.z < 0.25f);
            Console.WriteLine($"  normals={(shape.HasNormals() ? "yes" : "no")} count={(normals == null ? 0 : normals.Count)} degenerate={zero} tangents={(shape.HasTangents() ? "yes" : "no")}");

            var global = NifUtil.GlobalTransform(nif, shape);
            var tr = global.translation;
            Console.WriteLine($"  global: t=({tr.x:F2},{tr.y:F2},{tr.z:F2}) s={global.scale:F3} identity={NifUtil.IsIdentity(global)}");

            var verts = nif.GetVertsForShape(shape);
            if (verts.Count > 0)
                Console.WriteLine($"  local bounds: x[{verts.Min(v => v.x):F1},{verts.Max(v => v.x):F1}] y[{verts.Min(v => v.y):F1},{verts.Max(v => v.y):F1}] z[{verts.Min(v => v.z):F1},{verts.Max(v => v.z):F1}]");

            var uvs = nif.GetUvsForShape(shape);
            if (uvs.Count > 0)
                Console.WriteLine($"  uv: u[{uvs.Min(v => v.u):F3},{uvs.Max(v => v.u):F3}] v[{uvs.Min(v => v.v):F3},{uvs.Max(v => v.v):F3}]");
            // small shapes (billboard planes) are listed vertex by vertex
            if (verts.Count <= 8)
            {
                var colors = new vectorColor4();
                var hasColors = shape.HasVertexColors() && nif.GetColorsForShape(shape, colors) && colors.Count == verts.Count;
                for (var i = 0; i < verts.Count; i++)
                    Console.WriteLine($"    v{i}: ({verts[i].x:F2},{verts[i].y:F2},{verts[i].z:F2}) n=({normals![i].x:F2},{normals[i].y:F2},{normals[i].z:F2}) uv=({uvs[i].u:F2},{uvs[i].v:F2})" + (hasColors ? $" c=({colors[i].r:F2},{colors[i].g:F2},{colors[i].b:F2},{colors[i].a:F2})" : ""));
                var tris = new vectorTriangle();
                shape.GetTriangles(tris);
                Console.WriteLine("    tris: " + string.Join(" ", tris.Select(t => $"({t.p1},{t.p2},{t.p3})")));
            }

            var shader = NifUtil.ShaderOf(nif, shape);
            if (shader != null)
            {
                var line = $"  shader: {shader.GetBlockName()} SF1=0x{shader.shaderFlags1:X8} SF2=0x{shader.shaderFlags2:X8}";
                if (shader.GetBlockName() == "BSLightingShaderProperty")
                {
                    var lighting = NifUtil.Downcast<BSLightingShaderProperty>(shader);
                    line += $" type={lighting.GetShaderType()} clamp={lighting.textureClampMode} soft={lighting.softlighting:F2} rim={lighting.rimlightPower:F2} back={lighting.backlightPower:F2} gloss={lighting.glossiness:F1} spec={lighting.specularStrength:F2}";
                }
                Console.WriteLine(line);
                Console.WriteLine($"  tex0: {nif.GetTexturePathByIndex(shape, 0)}");
                Console.WriteLine($"  tex1: {nif.GetTexturePathByIndex(shape, 1)}");
                for (uint slot = 2; slot < 8; slot++)
                    if (nif.GetTexturePathByIndex(shape, slot) is { Length: > 0 } extra) Console.WriteLine($"  tex{slot}: {extra}");
            }

            var alpha = NifUtil.AlphaOf(nif, shape);
            Console.WriteLine(alpha != null ? $"  alpha: flags={alpha.flags} threshold={alpha.threshold}" : "  alpha: none");
        }
        return 0;
    }
}
