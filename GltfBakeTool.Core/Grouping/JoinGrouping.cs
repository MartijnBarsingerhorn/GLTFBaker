using System.Numerics;
using GltfBakeTool.Core.Scene;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Grouping;

/// <summary>Which material properties force separate joined meshes (one merged material cannot express them per part).</summary>
public sealed record GroupCriteria
{
    /// <summary>BLEND materials in their own group (opaque + mask merge fine as MASK).</summary>
    public bool SplitBlend { get; init; } = true;
    /// <summary>Also keep MASK apart from OPAQUE.</summary>
    public bool SplitMaskFromOpaque { get; init; } = false;
    /// <summary>KHR_materials_unlit vs lit.</summary>
    public bool SplitUnlit { get; init; } = true;
    /// <summary>KHR_materials_transmission / volume / diffuse transmission (real glass).</summary>
    public bool SplitTransmission { get; init; } = true;
    /// <summary>KHR_materials_clearcoat (car paint, lacquer) – until clearcoat baking exists.</summary>
    public bool SplitClearcoat { get; init; } = true;
    /// <summary>Sheen, specular, iridescence, anisotropy, IOR.</summary>
    public bool SplitOtherExtensions { get; init; } = false;
    /// <summary>Materials with textures on TEXCOORD_1+ (cannot be atlased).</summary>
    public bool SplitUvSet { get; init; } = true;
    /// <summary>Materials whose UVs tile more than <see cref="MaxTileRepeats"/> (would be clamped).</summary>
    public bool SplitHighTiling { get; init; } = false;
    public int MaxTileRepeats { get; init; } = 4;
    public bool SplitDoubleSided { get; init; } = false;
}

/// <summary>Compatibility key: primitives with equal keys can share one merged material.</summary>
public sealed record GroupKey(
    string Alpha,          // "opaque", "mask", "opaque/mask", "blend"
    bool Unlit,
    bool Transmission,
    bool Clearcoat,
    bool OtherExtensions,
    bool UvSet,
    bool HighTiling,
    bool DoubleSided,
    int Skin)              // logical skin index, -1 = rigid
{
    public string Label
    {
        get
        {
            var parts = new List<string> { Alpha };
            if (Unlit) parts.Add("unlit");
            if (Transmission) parts.Add("transmission");
            if (Clearcoat) parts.Add("clearcoat");
            if (OtherExtensions) parts.Add("ext");
            if (UvSet) parts.Add("uv1+");
            if (HighTiling) parts.Add("tiled");
            if (DoubleSided) parts.Add("2-sided");
            if (Skin >= 0) parts.Add($"skin#{Skin}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Short suffix for the joined node/mesh name.</summary>
    public string Suffix => Label.Replace(" · ", "_").Replace('/', '-').Replace("#", "");
}

public sealed class JoinGroup
{
    public required GroupKey Key { get; init; }
    public int Index { get; set; }
    public string Label => Key.Label;
    public List<Material?> Materials { get; } = new();
    public List<(Node Node, MeshPrimitive Primitive)> Primitives { get; } = new();
    public HashSet<Node> Nodes { get; } = new();
    /// <summary>Nodes whose primitives are not all in this group.</summary>
    public HashSet<Node> MixedNodes { get; } = new();
    public override string ToString() => $"{Label}: {Materials.Count} material(s), {Primitives.Count} primitive(s), {Nodes.Count} node(s)";
}

public static class JoinGrouping
{
    private static readonly string[] CoreChannels = { "BaseColor", "MetallicRoughness", "Normal", "Occlusion", "Emissive", "Diffuse", "SpecularGlossiness" };

    /// <summary>Groups all mesh primitives (optionally restricted to <paramref name="scope"/> nodes) by compatibility key.</summary>
    public static List<JoinGroup> Compute(ModelRoot model, GroupCriteria criteria, IEnumerable<Node>? scope = null)
    {
        var nodes = (scope ?? model.LogicalNodes).Where(n => n.Mesh != null).Distinct().ToList();
        var tiling = criteria.SplitHighTiling ? ComputeHighTiling(nodes, criteria.MaxTileRepeats) : new HashSet<Material>();

        var groups = new Dictionary<GroupKey, JoinGroup>();
        var nodeGroupCount = new Dictionary<Node, HashSet<GroupKey>>();
        foreach (var node in nodes)
        {
            foreach (var prim in node.Mesh!.Primitives)
            {
                var key = KeyOf(prim.Material, node, criteria, prim.Material != null && tiling.Contains(prim.Material));
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new JoinGroup { Key = key, Index = groups.Count };
                    groups[key] = g;
                }
                g.Primitives.Add((node, prim));
                g.Nodes.Add(node);
                if (!g.Materials.Contains(prim.Material)) g.Materials.Add(prim.Material);
                if (!nodeGroupCount.TryGetValue(node, out var set)) nodeGroupCount[node] = set = new();
                set.Add(key);
            }
        }
        foreach (var (node, keys) in nodeGroupCount)
            if (keys.Count > 1)
                foreach (var k in keys) groups[k].MixedNodes.Add(node);

        // stable order: largest group first
        var list = groups.Values.OrderByDescending(g => g.Primitives.Count).ToList();
        for (int i = 0; i < list.Count; i++) list[i].Index = i;
        return list;
    }

    public static GroupKey KeyOf(Material? m, Node node, GroupCriteria c, bool highTiling)
    {
        var alpha = m?.Alpha ?? AlphaMode.OPAQUE;
        string alphaClass;
        if (!c.SplitBlend && !c.SplitMaskFromOpaque) alphaClass = "any-alpha";
        else if (alpha == AlphaMode.BLEND) alphaClass = c.SplitBlend ? "blend" : "opaque/mask";
        else if (alpha == AlphaMode.MASK) alphaClass = c.SplitMaskFromOpaque ? "mask" : "opaque/mask";
        else alphaClass = c.SplitMaskFromOpaque ? "opaque" : "opaque/mask";

        var ext = ExtensionsOf(m);
        return new GroupKey(
            Alpha: alphaClass,
            Unlit: c.SplitUnlit && m?.Unlit == true,
            Transmission: c.SplitTransmission && (ext.Contains("KHR_materials_transmission") || ext.Contains("KHR_materials_volume") || ext.Contains("KHR_materials_diffuse_transmission")),
            Clearcoat: c.SplitClearcoat && ext.Contains("KHR_materials_clearcoat"),
            OtherExtensions: c.SplitOtherExtensions && ext.Any(e => e is "KHR_materials_sheen" or "KHR_materials_specular" or "KHR_materials_iridescence" or "KHR_materials_anisotropy" or "KHR_materials_ior" or "KHR_materials_dispersion"),
            UvSet: c.SplitUvSet && UsesSecondaryUvSet(m),
            HighTiling: c.SplitHighTiling && highTiling,
            DoubleSided: c.SplitDoubleSided && m?.DoubleSided == true,
            Skin: node.Skin?.LogicalIndex ?? -1);
    }

    /// <summary>KHR material extensions in use (by non-default content), derived from SharpGLTF's channel keys.</summary>
    public static HashSet<string> ExtensionsOf(Material? m)
    {
        var set = new HashSet<string>();
        if (m == null) return set;
        if (m.Unlit) set.Add("KHR_materials_unlit");
        if (MathF.Abs(m.IndexOfRefraction - 1.5f) > 1e-4f) set.Add("KHR_materials_ior");
        if (m.Dispersion != 0) set.Add("KHR_materials_dispersion");
        foreach (var ch in m.Channels)
        {
            if (CoreChannels.Contains(ch.Key)) continue;
            if (ch.HasDefaultContent && ch.Texture == null) continue;
            var name = ExtensionOfChannel(ch.Key);
            if (name != null) set.Add(name);
        }
        if (m.FindChannel("SpecularGlossiness") != null || m.FindChannel("Diffuse") != null) set.Add("KHR_materials_pbrSpecularGlossiness");
        return set;
    }

    public static string? ExtensionOfChannel(string key)
    {
        if (key.StartsWith("ClearCoat", StringComparison.Ordinal)) return "KHR_materials_clearcoat";
        if (key.StartsWith("DiffuseTransmission", StringComparison.Ordinal)) return "KHR_materials_diffuse_transmission";
        if (key.StartsWith("Transmission", StringComparison.Ordinal)) return "KHR_materials_transmission";
        if (key.StartsWith("Volume", StringComparison.Ordinal) || key.StartsWith("Attenuation", StringComparison.Ordinal)) return "KHR_materials_volume";
        if (key.StartsWith("Sheen", StringComparison.Ordinal)) return "KHR_materials_sheen";
        if (key.StartsWith("Specular", StringComparison.Ordinal)) return "KHR_materials_specular";
        if (key.StartsWith("Iridescence", StringComparison.Ordinal)) return "KHR_materials_iridescence";
        if (key.StartsWith("Anisotropy", StringComparison.Ordinal)) return "KHR_materials_anisotropy";
        return null;
    }

    public static bool UsesSecondaryUvSet(Material? m)
        => m != null && m.Channels.Any(ch => ch.Texture != null && ch.TextureCoordinate != 0);

    /// <summary>Materials whose TEXCOORD_0 range (after texture transform) tiles more than <paramref name="maxRepeats"/> per axis.</summary>
    public static HashSet<Material> ComputeHighTiling(IEnumerable<Node> nodes, int maxRepeats)
    {
        var min = new Dictionary<Material, Vector2>();
        var max = new Dictionary<Material, Vector2>();
        foreach (var node in nodes)
        {
            foreach (var prim in node.Mesh!.Primitives)
            {
                var m = prim.Material;
                if (m == null || !m.Channels.Any(c => c.Texture != null)) continue;
                var uvAcc = prim.GetVertexAccessor("TEXCOORD_0");
                if (uvAcc == null) continue;
                var xf = (m.FindChannel("BaseColor") ?? m.FindChannel("Diffuse"))?.TextureTransform?.Matrix ?? Matrix3x2.Identity;
                var uvs = uvAcc.AsVector2Array();
                var used = new HashSet<int>();
                foreach (var (a, b, c) in prim.GetTriangleIndices()) { used.Add(a); used.Add(b); used.Add(c); }
                var lo = min.TryGetValue(m, out var l) ? l : new Vector2(float.MaxValue);
                var hi = max.TryGetValue(m, out var h) ? h : new Vector2(float.MinValue);
                foreach (int i in used)
                {
                    var uv = Vector2.Transform(uvs[i], xf);
                    lo = Vector2.Min(lo, uv); hi = Vector2.Max(hi, uv);
                }
                min[m] = lo; max[m] = hi;
            }
        }
        var result = new HashSet<Material>();
        foreach (var m in min.Keys)
        {
            float ru = max[m].X - min[m].X, rv = max[m].Y - min[m].Y;
            if (ru > maxRepeats || rv > maxRepeats) result.Add(m);
        }
        return result;
    }
}
