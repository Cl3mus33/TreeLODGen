using nifly;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TreeLODGen;

static class NifUtil
{
    public const uint SLSF1_Skinned = 0x00000002;
    public const uint SLSF2_VertexColors = 0x00000020;
    public const uint SLSF2_TreeAnim = 0x20000000;

    public static T Downcast<T>(object obj) where T : class
    {
        var cPtrField = obj.GetType().GetField("swigCPtr", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handleRef = (HandleRef)cPtrField.GetValue(obj)!;
        var ctor = typeof(T).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IntPtr), typeof(bool) }, null)!;
        return (T)ctor.Invoke(new object[] { handleRef.Handle, false });
    }

    // Shapes can sit several nodes deep (BSTreeNode > BSMultiBoundNode > NiSwitchNode > ...),
    // so the transform to bake into a flattened LOD NIF is the parent chain -- except the root:
    // the game replaces the root node's transform with the reference's placement, so a scale
    // or offset there (common in Ulvenwald) never shows in game and must not reach the LOD.
    public static MatTransform GlobalTransform(NifFile nif, NiAVObject obj)
    {
        var result = obj.transform;
        var parent = nif.GetParentNode(obj);
        while (parent != null && nif.GetParentNode(parent) != null)
        {
            result = parent.transform.ComposeTransforms(result);
            parent = nif.GetParentNode(parent);
        }
        return result;
    }

    public static string NodePath(NifFile nif, NiAVObject obj)
    {
        var parts = new List<string>();
        var parent = nif.GetParentNode(obj);
        while (parent != null)
        {
            parts.Add($"{parent.GetBlockName()}'{parent.name.get()}'");
            parent = nif.GetParentNode(parent);
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    public static bool IsIdentity(MatTransform t)
    {
        var identity = new MatTransform();
        return t.IsNearlyEqualTo(identity);
    }

    // Moves a shape's transform into its vertices and normals, leaving the shape at the
    // origin. LOD models shipped with DynDOLOD all look like that; a scale left on the shape
    // is not reliably applied by LODGen, which drew such trees at the wrong size.
    public static void BakeTransform(NifFile nif, NiShape shape)
    {
        var t = shape.transform;
        if (IsIdentity(t)) return;
        var verts = nif.GetVertsForShape(shape);
        var normals = nif.GetNormalsForShape(shape);
        nif.SetVertsForShape(shape, new vectorVector3(verts.Select(v => t.ApplyTransform(v)).ToList()));
        if (normals != null && normals.Count == verts.Count)
        {
            nif.SetNormalsForShape(shape, new vectorVector3(normals.Select(n =>
            {
                var d = t.ApplyTransformToDir(n);
                var length = MathF.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
                return length > 1e-6f ? new Vector3(d.x / length, d.y / length, d.z / length) : n;
            }).ToList()));
        }
        shape.transform = new MatTransform();
        nif.CalcTangentsForShape(shape);
        shape.UpdateBounds();
    }

    public static BSShaderProperty? ShaderOf(NifFile nif, NiShape shape)
    {
        var shader = nif.GetShader(shape);
        return shader == null ? null : Downcast<BSShaderProperty>(shader);
    }

    public static NiAlphaProperty? AlphaOf(NifFile nif, NiShape shape)
    {
        return shape.HasAlphaProperty() ? nif.GetAlphaProperty(shape) : null;
    }

    public static uint Crc32(byte[] data)
    {
        return System.IO.Hashing.Crc32.HashToUInt32(data);
    }
}
