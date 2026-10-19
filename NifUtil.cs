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

    const uint SLSF1_Specular = 1u << 0, SLSF1_EnvironmentMapping = 1u << 7, SLSF1_Parallax = 1u << 11,
        SLSF1_EyeEnvironmentMapping = 1u << 17, SLSF1_ParallaxOcclusion = 1u << 28;
    const uint SLSF2_GlowMap = 1u << 6, SLSF2_EnvMapLightFade = 1u << 15, SLSF2_Pbr = 1u << 23,
        SLSF2_MultiLayerParallax = 1u << 24, SLSF2_SoftLighting = 1u << 25, SLSF2_RimLighting = 1u << 26,
        SLSF2_BackLighting = 1u << 27;
    // BSLightingShaderType values that need texture slots or effects a LOD model does not have
    static readonly uint[] SpecialShaderTypes = { 1, 2, 3, 7, 11, 16 }; // envmap, glow, parallax, parallax occ., multilayer, eye envmap

    // A LOD model gets a plain lighting shader: its diffuse and normal map, and nothing that the
    // object LOD renderer does not handle. PGPatcher can turn the full tree into PBR (SLSF2
    // Unused01, RMAOS and the like in slots 2-7), Complex Material (environment map) or
    // parallax; copied as they are onto the LOD, those made distant trees glow. With PBR the
    // normal map's alpha is not a specular mask, so specular goes too. Returns what was removed.
    public static string? PlainLodShader(NifFile nif, NiShape shape)
    {
        if (ShaderOf(nif, shape) is not { } shader) return null;
        uint f1 = shader.shaderFlags1, f2 = shader.shaderFlags2, type = shader.GetShaderType();
        var pbr = (f2 & SLSF2_Pbr) != 0;
        var removed = new List<string>();
        if (pbr) removed.Add("PBR");
        if ((f1 & (SLSF1_EnvironmentMapping | SLSF1_EyeEnvironmentMapping)) != 0 || type is 1 or 16) removed.Add("environment map");
        if ((f1 & (SLSF1_Parallax | SLSF1_ParallaxOcclusion)) != 0 || (f2 & SLSF2_MultiLayerParallax) != 0 || type is 3 or 7 or 11) removed.Add("parallax");
        if ((f2 & SLSF2_GlowMap) != 0 || type == 2) removed.Add("glow map");

        f1 &= ~(SLSF1_EnvironmentMapping | SLSF1_Parallax | SLSF1_EyeEnvironmentMapping | SLSF1_ParallaxOcclusion);
        f2 &= ~(SLSF2_GlowMap | SLSF2_EnvMapLightFade | SLSF2_Pbr | SLSF2_MultiLayerParallax);
        if (pbr) f1 &= ~SLSF1_Specular;
        if (removed.Count == 0) return null;

        shader.shaderFlags1 = f1;
        shader.shaderFlags2 = f2;
        if (Array.IndexOf(SpecialShaderTypes, type) >= 0) shader.SetShaderType(0);
        // slot 2 (rim/soft lighting map) and 7 (back lighting map) stay when their effect is
        // still on and the textures are not PBR ones; every other slot past the normal map goes
        for (uint slot = 2; slot < 8; slot++)
        {
            var keep = !pbr && ((slot == 2 && (f2 & (SLSF2_RimLighting | SLSF2_SoftLighting)) != 0) || (slot == 7 && (f2 & SLSF2_BackLighting) != 0));
            if (!keep && nif.GetTexturePathByIndex(shape, slot) != "") nif.SetTextureSlot(shape, "", slot);
        }
        return string.Join("+", removed);
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
