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
    // so the transform to bake into a flattened LOD NIF is the whole parent chain.
    public static MatTransform GlobalTransform(NifFile nif, NiAVObject obj)
    {
        var result = obj.transform;
        var parent = nif.GetParentNode(obj);
        while (parent != null)
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
