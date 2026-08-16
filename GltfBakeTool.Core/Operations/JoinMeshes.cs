using System.Numerics;
using GltfBakeTool.Core.Atlas;
using GltfBakeTool.Core.Scene;
using GltfBakeTool.Core.Structure;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Operations;

public sealed record JoinOptions
{
    public string Name { get; init; } = "Joined";
    public AtlasOptions Atlas { get; init; } = new();
    /// <summary>Remove the source mesh nodes (and the empties they leave behind) after joining.</summary>
    public bool RemoveSources { get; init; } = true;
}

public sealed class JoinReport
{
    public List<string> Warnings { get; } = new();
    public int SourceNodes { get; set; }
    public int SourcePrimitives { get; set; }
    public int SourceMaterials { get; set; }
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public List<string> Channels { get; } = new();
    public string? PruneSummary { get; set; }
    public int NewNodeIndex { get; set; } = -1;
    /// <summary>Per source material: where it landed in the atlas.</summary>
    public List<string> CellTable { get; } = new();
    public override string ToString()
        => $"joined {SourcePrimitives} primitive(s) from {SourceNodes} node(s), {SourceMaterials} material(s) → 1 primitive, {Vertices:N0} vertices, {Triangles:N0} triangles, atlas {AtlasWidth}×{AtlasHeight} [{string.Join(", ", Channels)}]"
         + (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "");
}

/// <summary>Merges the meshes under the selected nodes into one primitive with one atlased material.</summary>
public static class JoinMeshes
{
    private sealed class SourcePrim
    {
        public required Node Node;
        public required MeshPrimitive Prim;
        public required Matrix4x4 World;
        public required int UsageIndex;
        public Matrix3x2 UvTransform = Matrix3x2.Identity;
        public List<(int A, int B, int C)> Triangles = new();
    }

    public static ModelRoot Run(ModelRoot model, IReadOnlyCollection<int> nodeIndices, JoinOptions options, out JoinReport report)
    {
        report = new JoinReport();
        var warnings = report.Warnings;

        // ---- gather source nodes -------------------------------------------------------------
        var selected = nodeIndices.Select(i => model.LogicalNodes[i]).ToList();
        var sourceNodes = selected.SelectMany(GeometryExtractor.Flatten).Distinct().Where(n => n.Mesh != null).ToList();
        if (sourceNodes.Count == 0) throw new InvalidOperationException("Selection contains no meshes.");

        var skins = sourceNodes.Select(n => n.Skin).Distinct().ToList();
        Skin? skin = null;
        if (skins.Count == 1 && skins[0] != null) skin = skins[0];
        else if (skins.Count > 1)
            throw new InvalidOperationException(skins.Any(s => s == null)
                ? "Selection mixes skinned and rigid meshes; join them separately."
                : "Selection contains meshes bound to different skins; only meshes sharing one skin can be joined.");

        // ---- where the joined node will live: below the common ancestor of the sources -----------
        var parent = CommonAncestor(sourceNodes);
        if (parent != null && sourceNodes.Contains(parent)) parent = parent.VisualParent;
        Matrix4x4.Invert(parent?.WorldMatrix ?? Matrix4x4.Identity, out var parentInverse);

        // ---- collect primitives ---------------------------------------------------------------
        var usages = new List<MaterialUsage>();
        var usageIndex = new Dictionary<Material, int>();
        int defaultUsage = -1;
        var prims = new List<SourcePrim>();

        foreach (var node in sourceNodes)
        {
            // geometry is baked into the join parent's local space (skinned: bind space, untouched)
            var world = skin != null ? Matrix4x4.Identity : node.WorldMatrix * parentInverse;
            foreach (var prim in node.Mesh!.Primitives)
            {
                if (prim.DrawPrimitiveType is PrimitiveType.POINTS or PrimitiveType.LINES or PrimitiveType.LINE_LOOP or PrimitiveType.LINE_STRIP)
                {
                    warnings.Add($"'{Name(node)}': primitive {prim.LogicalIndex} is {prim.DrawPrimitiveType}; skipped (only triangles are joined).");
                    continue;
                }
                if (prim.MorphTargetsCount > 0)
                {
                    warnings.Add($"'{Name(node)}': primitive {prim.LogicalIndex} has morph targets; skipped.");
                    continue;
                }
                if (prim.GetVertexAccessor("POSITION") == null) continue;

                int ui;
                if (prim.Material == null)
                {
                    if (defaultUsage < 0) { defaultUsage = usages.Count; usages.Add(new MaterialUsage { Material = null }); }
                    ui = defaultUsage;
                }
                else if (!usageIndex.TryGetValue(prim.Material, out ui))
                {
                    ui = usages.Count;
                    usageIndex[prim.Material] = ui;
                    usages.Add(new MaterialUsage { Material = prim.Material });
                }

                var sp = new SourcePrim { Node = node, Prim = prim, World = world, UsageIndex = ui };
                sp.Triangles = prim.GetTriangleIndices().ToList();
                if (sp.Triangles.Count == 0) continue;
                sp.UvTransform = UvTransformOf(prim.Material, warnings);
                prims.Add(sp);
            }
        }
        if (prims.Count == 0) throw new InvalidOperationException("No joinable triangle primitives in the selection.");

        // skipped primitives keep their node; the join must not clear those meshes.
        var consumedNodes = prims.Select(p => p.Node).Distinct().ToList();
        var partiallyConsumed = consumedNodes.Where(n => n.Mesh!.Primitives.Any(pr => !prims.Any(sp => sp.Prim == pr))).ToList();
        foreach (var n in partiallyConsumed)
            warnings.Add($"'{Name(n)}': some primitives were skipped, so the node keeps its original mesh (joined primitives now exist twice).");

        // ---- UV bounds per material (with texture transform applied) ---------------------------
        foreach (var sp in prims)
        {
            var uvAcc = sp.Prim.GetVertexAccessor("TEXCOORD_0");
            if (uvAcc == null) continue;
            var uvs = uvAcc.AsVector2Array();
            var usage = usages[sp.UsageIndex];
            var used = new HashSet<int>();
            foreach (var (a, b, c) in sp.Triangles) { used.Add(a); used.Add(b); used.Add(c); }
            foreach (int i in used) usage.Include(Vector2.Transform(uvs[i], sp.UvTransform));
        }

        // ---- extensions the merged (core PBR) material cannot carry --------------------------------
        foreach (var u in usages)
        {
            if (u.Material == null) continue;
            var ext = Grouping.JoinGrouping.ExtensionsOf(u.Material);
            var mname = string.IsNullOrEmpty(u.Material.Name) ? $"material #{u.Material.LogicalIndex}" : u.Material.Name;
            if (ext.Remove("KHR_materials_pbrSpecularGlossiness"))
                warnings.Add($"'{mname}': spec-gloss material approximated (diffuse → base colour, metallic 0, roughness = 1 − glossiness).");
            if (ext.Count > 0)
                warnings.Add($"'{mname}': {string.Join(", ", ext.OrderBy(x => x))} not carried over by the merged material.");
        }

        // ---- bake atlas ------------------------------------------------------------------------
        var atlas = MaterialAtlasBaker.Bake(usages, options.Atlas);
        warnings.AddRange(atlas.Warnings);
        report.AtlasWidth = atlas.Width;
        report.AtlasHeight = atlas.Height;
        report.Channels.AddRange(atlas.Images.Keys.Select(k => k.ToString()));
        for (int i = 0; i < usages.Count; i++)
        {
            var c = atlas.Cells[i];
            var mname = usages[i].Material == null ? "<default>" : (string.IsNullOrEmpty(usages[i].Material!.Name) ? $"#{usages[i].Material!.LogicalIndex}" : usages[i].Material!.Name);
            report.CellTable.Add($"{mname}: {c.Content.W}×{c.Content.H} @ ({c.Content.X},{c.Content.Y})" + (c.Solid ? " solid" : $" repeats {c.RepeatsU}×{c.RepeatsV}") + (c.Clamped ? " CLAMPED" : ""));
        }

        // ---- merge geometry --------------------------------------------------------------------
        bool anyColor = prims.Any(p => p.Prim.GetVertexAccessor("COLOR_0") != null);
        bool allTangent = prims.All(p => p.Prim.GetVertexAccessor("TANGENT") != null);
        bool skinned = skin != null;
        if (skinned && !prims.All(p => p.Prim.GetVertexAccessor("JOINTS_0") != null && p.Prim.GetVertexAccessor("WEIGHTS_0") != null))
            throw new InvalidOperationException("A skinned primitive lacks JOINTS_0/WEIGHTS_0.");

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs0 = new List<Vector2>();
        var colors = anyColor ? new List<Vector4>() : null;
        var tangents = allTangent ? new List<Vector4>() : null;
        var joints = skinned ? new List<Vector4>() : null;
        var weights = skinned ? new List<Vector4>() : null;
        var indices = new List<int>();

        foreach (var sp in prims)
        {
            int baseIndex = positions.Count;
            var srcPos = sp.Prim.GetVertexAccessor("POSITION")!.AsVector3Array();
            int count = srcPos.Count;
            var world = sp.World;
            Matrix4x4.Invert(world, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);
            bool flip = world.GetDeterminant() < 0;

            var cell = atlas.Cells[sp.UsageIndex];
            var srcNrm = sp.Prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var srcUv = sp.Prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var srcCol = sp.Prim.GetVertexAccessor("COLOR_0")?.AsColorArray();
            var srcTan = tangents != null ? sp.Prim.GetVertexAccessor("TANGENT")!.AsVector4Array() : null;
            var srcJ = skinned ? sp.Prim.GetVertexAccessor("JOINTS_0")!.AsVector4Array() : null;
            var srcW = skinned ? sp.Prim.GetVertexAccessor("WEIGHTS_0")!.AsVector4Array() : null;

            Vector3[]? computedNormals = null;
            if (srcNrm == null)
            {
                computedNormals = ComputeNormals(srcPos, sp.Triangles);
                warnings.Add($"'{Name(sp.Node)}': primitive {sp.Prim.LogicalIndex} has no normals; smooth normals were generated.");
            }

            for (int i = 0; i < count; i++)
            {
                positions.Add(Vector3.Transform(srcPos[i], world));
                var n = srcNrm != null ? srcNrm[i] : computedNormals![i];
                n = Vector3.TransformNormal(n, normalMatrix);
                normals.Add(n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY);

                var uv = srcUv != null ? Vector2.Transform(srcUv[i], sp.UvTransform) : Vector2.Zero;
                uvs0.Add(cell.MapUv(uv, atlas.Width, atlas.Height));

                colors?.Add(srcCol != null ? srcCol[i] : Vector4.One);
                if (tangents != null)
                {
                    var t = srcTan![i];
                    var t3 = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), world);
                    if (t3.LengthSquared() > 0) t3 = Vector3.Normalize(t3);
                    tangents.Add(new Vector4(t3, flip ? -t.W : t.W));
                }
                if (skinned) { joints!.Add(srcJ![i]); weights!.Add(srcW![i]); }
            }
            foreach (var (a, b, c) in sp.Triangles)
            {
                indices.Add(baseIndex + a);
                indices.Add(baseIndex + (flip ? c : b));
                indices.Add(baseIndex + (flip ? b : c));
            }
        }

        report.SourceNodes = consumedNodes.Count;
        report.SourcePrimitives = prims.Count;
        report.SourceMaterials = usages.Count;
        report.Vertices = positions.Count;
        report.Triangles = indices.Count / 3;

        // ---- author material -------------------------------------------------------------------
        var material = CreateMaterial(model, options.Name, atlas);

        // ---- author mesh -----------------------------------------------------------------------
        var mesh = model.CreateMesh(options.Name);
        var newPrim = mesh.CreatePrimitive()
            .WithVertexAccessor("POSITION", positions)
            .WithVertexAccessor("NORMAL", normals)
            .WithVertexAccessor("TEXCOORD_0", uvs0)
            .WithIndicesAccessor(PrimitiveType.TRIANGLES, indices)
            .WithMaterial(material);
        if (colors != null) newPrim.WithVertexAccessor("COLOR_0", colors);
        if (tangents != null) newPrim.WithVertexAccessor("TANGENT", tangents);
        if (skinned)
        {
            newPrim.WithVertexAccessor(CreateJointsAccessor(joints!, skin!.JointsCount));
            newPrim.WithVertexAccessor("WEIGHTS_0", weights!);
        }

        // ---- place node ------------------------------------------------------------------------
        var scene = model.DefaultScene ?? model.LogicalScenes.First();

        foreach (var n in consumedNodes)
            for (var a = n; a != null && a != parent; a = a.VisualParent)
                if (a.IsTransformAnimated) { warnings.Add($"'{Name(a)}' is animated below the join parent; its animation is baked at rest pose into the joined mesh."); break; }

        var newNode = parent != null ? parent.CreateNode(options.Name) : scene.CreateNode(options.Name);
        newNode.LocalMatrix = Matrix4x4.Identity;
        if (skinned) newNode.Skin = skin;
        newNode.Mesh = mesh;
        report.NewNodeIndex = newNode.LogicalIndex;

        // ---- detach sources ---------------------------------------------------------------------
        var toClear = consumedNodes.Except(partiallyConsumed).ToList();
        foreach (var n in toClear)
        {
            n.Mesh = null;
            n.Skin = null;
        }

        // ---- structural clean-up: drop emptied nodes, prune orphaned resources -------------------
        var pkg = GlbPackage.FromModel(model);
        if (options.RemoveSources)
        {
            var scope = new HashSet<int>();
            foreach (var n in toClear)
                for (var a = n; a != null && a != parent; a = a.VisualParent) scope.Add(a.LogicalIndex);
            var removable = CleanEmptyNodes.FindRemovable(model, new CleanEmptyNodesOptions { OnlyNodes = scope, FoldNonIdentityTransforms = false });
            // an emptied node with a non-identity transform and content children cannot be folded safely; leave it
            GltfStructure.RemoveNodes(pkg, removable.Select(n => n.LogicalIndex).ToList(), foldTransforms: false);
        }
        var prune = GltfStructure.PruneUnused(pkg);
        report.PruneSummary = prune.ToString();

        var result = pkg.ToModel();
        // node index of the new node may have shifted; find it by name+mesh
        report.NewNodeIndex = result.LogicalNodes.FirstOrDefault(n => n.Name == options.Name && n.Mesh != null)?.LogicalIndex ?? -1;
        return result;
    }

    // -------------------------------------------------------------------------------------------

    private static Material CreateMaterial(ModelRoot model, string name, AtlasResult atlas)
    {
        var mat = model.CreateMaterial(name + "_atlas").WithPBRMetallicRoughness();
        mat.Alpha = atlas.Alpha;
        if (atlas.Alpha == AlphaMode.MASK) mat.AlphaCutoff = atlas.AlphaCutoff;
        mat.DoubleSided = atlas.DoubleSided;

        Image Img(AtlasChannel ch) => model.UseImage(new MemoryImage(atlas.Images[ch]));

        var baseCh = mat.FindChannel("BaseColor")!.Value;
        baseCh.Color = Vector4.One;
        if (atlas.Images.ContainsKey(AtlasChannel.BaseColor))
            baseCh.SetTexture(0, Img(AtlasChannel.BaseColor), null, TextureWrapMode.CLAMP_TO_EDGE, TextureWrapMode.CLAMP_TO_EDGE);

        var mrCh = mat.FindChannel("MetallicRoughness")!.Value;
        if (atlas.Images.ContainsKey(AtlasChannel.MetallicRoughness))
        {
            mrCh.SetFactor("MetallicFactor", 1f);
            mrCh.SetFactor("RoughnessFactor", 1f);
            mrCh.SetTexture(0, Img(AtlasChannel.MetallicRoughness), null, TextureWrapMode.CLAMP_TO_EDGE, TextureWrapMode.CLAMP_TO_EDGE);
        }
        else
        {
            mrCh.SetFactor("MetallicFactor", atlas.UniformMetallic ?? 1f);
            mrCh.SetFactor("RoughnessFactor", atlas.UniformRoughness ?? 1f);
        }

        if (atlas.Images.ContainsKey(AtlasChannel.Normal))
            mat.FindChannel("Normal")!.Value.SetTexture(0, Img(AtlasChannel.Normal), null, TextureWrapMode.CLAMP_TO_EDGE, TextureWrapMode.CLAMP_TO_EDGE);
        if (atlas.Images.ContainsKey(AtlasChannel.Occlusion))
            mat.FindChannel("Occlusion")!.Value.SetTexture(0, Img(AtlasChannel.Occlusion), null, TextureWrapMode.CLAMP_TO_EDGE, TextureWrapMode.CLAMP_TO_EDGE);

        var emCh = mat.FindChannel("Emissive")!.Value;
        if (atlas.Images.ContainsKey(AtlasChannel.Emissive))
        {
            emCh.Color = Vector4.One;
            emCh.SetTexture(0, Img(AtlasChannel.Emissive), null, TextureWrapMode.CLAMP_TO_EDGE, TextureWrapMode.CLAMP_TO_EDGE);
        }
        else if (atlas.UniformEmissive is { } e && e != Vector3.Zero)
            emCh.Color = new Vector4(e, 1);

        return mat;
    }

    private static MemoryAccessor CreateJointsAccessor(List<Vector4> joints, int jointCount)
    {
        var enc = jointCount > 255 ? EncodingType.UNSIGNED_SHORT : EncodingType.UNSIGNED_BYTE;
        int stride = enc == EncodingType.UNSIGNED_SHORT ? 8 : 4;
        var info = new MemoryAccessInfo("JOINTS_0", 0, joints.Count, stride, DimensionType.VEC4, enc, false);
        var acc = new MemoryAccessor(new byte[joints.Count * stride], info);
        var arr = acc.AsVector4Array();
        for (int i = 0; i < joints.Count; i++) arr[i] = joints[i];
        return acc;
    }

    private static Matrix3x2 UvTransformOf(Material? m, List<string> warnings)
    {
        if (m == null) return Matrix3x2.Identity;
        var baseCh = m.FindChannel("BaseColor") ?? m.FindChannel("Diffuse");
        var xf = baseCh?.TextureTransform;
        var result = xf?.Matrix ?? Matrix3x2.Identity;
        foreach (var ch in m.Channels)
        {
            var other = ch.TextureTransform?.Matrix ?? Matrix3x2.Identity;
            if (ch.Texture != null && other != result)
            {
                warnings.Add($"'{m.Name}': channel {ch.Key} has a different KHR_texture_transform than the base colour; the base colour transform was applied to all channels.");
                break;
            }
        }
        return result;
    }

    private static Vector3[] ComputeNormals(IReadOnlyList<Vector3> pos, List<(int A, int B, int C)> tris)
    {
        var n = new Vector3[pos.Count];
        foreach (var (a, b, c) in tris)
        {
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            n[a] += fn; n[b] += fn; n[c] += fn;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitY;
        return n;
    }

    private static Node? CommonAncestor(IReadOnlyList<Node> nodes)
    {
        List<Node> Chain(Node n) { var l = new List<Node>(); for (var a = n; a != null; a = a.VisualParent) l.Add(a); l.Reverse(); return l; }
        var chains = nodes.Select(Chain).ToList();
        Node? common = null;
        for (int depth = 0; ; depth++)
        {
            if (chains.Any(c => c.Count <= depth)) break;
            var candidate = chains[0][depth];
            if (chains.All(c => c[depth] == candidate)) common = candidate; else break;
        }
        return common;
    }

    private static string Name(Node n) => string.IsNullOrEmpty(n.Name) ? $"<node {n.LogicalIndex}>" : n.Name;
}
