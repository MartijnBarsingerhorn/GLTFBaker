using System.Numerics;
using GltfBakeTool.Core.Atlas;
using GltfBakeTool.Core.Grouping;
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
    /// <summary>When set, the selection is partitioned by material compatibility and one mesh is produced per group.</summary>
    public GroupCriteria? Grouping { get; init; }
}

/// <summary>Result of one merged mesh (one per join group).</summary>
public sealed class JoinGroupReport
{
    public string Label { get; set; } = "";
    public string NodeName { get; set; } = "";
    public int SourceNodes { get; set; }
    public int SourcePrimitives { get; set; }
    public int SourceMaterials { get; set; }
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public List<string> Channels { get; } = new();
    /// <summary>Distinct atlas cells (materials with identical textures share one).</summary>
    public int UniqueCells { get; set; }
    public List<string> CellTable { get; } = new();
    public List<string> Warnings { get; } = new();
    public int NewNodeIndex { get; set; } = -1;
    public override string ToString()
        => $"'{NodeName}'{(Label.Length > 0 ? $" [{Label}]" : "")}: {SourcePrimitives} primitive(s), {SourceMaterials} material(s) → {Vertices:N0} vertices, {Triangles:N0} triangles, atlas {AtlasWidth}×{AtlasHeight} ({UniqueCells} cells) [{string.Join(", ", Channels)}]"
         + (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "");
}

public sealed class JoinReport
{
    public List<JoinGroupReport> Groups { get; } = new();
    /// <summary>Warnings not tied to a group (skipped primitives, structural notes).</summary>
    public List<string> Warnings { get; } = new();
    public string? PruneSummary { get; set; }
    public int SourceNodes { get; set; }
    public int SourcePrimitives => Groups.Sum(g => g.SourcePrimitives);
    public IEnumerable<string> AllWarnings => Warnings.Concat(Groups.SelectMany(g => g.Warnings));
    public override string ToString()
    {
        int w = AllWarnings.Count();
        string tail = w > 0 ? $", {w} warning(s)" : "";
        if (Groups.Count == 1)
        {
            var g = Groups[0];
            return $"joined {g.SourcePrimitives} primitive(s) from {SourceNodes} node(s), {g.SourceMaterials} material(s) → 1 primitive, {g.Vertices:N0} vertices, {g.Triangles:N0} triangles, atlas {g.AtlasWidth}×{g.AtlasHeight} [{string.Join(", ", g.Channels)}]{tail}";
        }
        return $"joined {SourcePrimitives} primitive(s) from {SourceNodes} node(s) into {Groups.Count} meshes{tail}";
    }
}

/// <summary>Merges the meshes under the selected nodes into one primitive + one atlased material per compatibility group.</summary>
public static class JoinMeshes
{
    private sealed class SourcePrim
    {
        public required Node Node;
        public required MeshPrimitive Prim;
        public Matrix4x4 World;
        public int UsageIndex;
        public Matrix3x2 UvTransform = Matrix3x2.Identity;
        public List<(int A, int B, int C)> Triangles = new();
        /// <summary>TEXCOORD_0 after texture transform and per-island integer shifts (null: no UVs).</summary>
        public Vector2[]? Uvs;
    }

    public static ModelRoot Run(ModelRoot model, IReadOnlyCollection<int> nodeIndices, JoinOptions options, out JoinReport report)
    {
        report = new JoinReport();
        var warnings = report.Warnings;

        // ---- gather source primitives -------------------------------------------------------------
        var selected = nodeIndices.Select(i => model.LogicalNodes[i]).ToList();
        var sourceNodes = selected.SelectMany(GeometryExtractor.Flatten).Distinct().Where(n => n.Mesh != null).ToList();
        if (sourceNodes.Count == 0) throw new InvalidOperationException("Selection contains no meshes.");

        var prims = new List<SourcePrim>();
        foreach (var node in sourceNodes)
        {
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
                var sp = new SourcePrim { Node = node, Prim = prim, Triangles = prim.GetTriangleIndices().ToList() };
                if (sp.Triangles.Count == 0) continue;
                prims.Add(sp);
            }
        }
        if (prims.Count == 0) throw new InvalidOperationException("No joinable triangle primitives in the selection.");

        // ---- partition into groups -----------------------------------------------------------------
        List<List<SourcePrim>> groups;
        List<string> groupLabels;
        if (options.Grouping is { } criteria)
        {
            var tiling = criteria.SplitHighTiling ? JoinGrouping.ComputeHighTiling(sourceNodes, criteria.MaxTileRepeats) : new HashSet<Material>();
            var byKey = new Dictionary<GroupKey, List<SourcePrim>>();
            foreach (var sp in prims)
            {
                var key = JoinGrouping.KeyOf(sp.Prim.Material, sp.Node, criteria, sp.Prim.Material != null && tiling.Contains(sp.Prim.Material));
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new();
                list.Add(sp);
            }
            var ordered = byKey.OrderByDescending(kv => kv.Value.Count).ToList();
            groups = ordered.Select(kv => kv.Value).ToList();
            groupLabels = ordered.Select(kv => kv.Key.Label).ToList();
        }
        else
        {
            var skins = prims.Select(p => p.Node.Skin).Distinct().ToList();
            if (skins.Count > 1)
                throw new InvalidOperationException(skins.Any(s => s == null)
                    ? "Selection mixes skinned and rigid meshes; join them separately (or use 'Join per group')."
                    : "Selection contains meshes bound to different skins; only meshes sharing one skin can be joined into one mesh (use 'Join per group').");
            groups = new() { prims };
            groupLabels = new() { "" };
        }

        var consumedNodes = prims.Select(p => p.Node).Distinct().ToList();
        report.SourceNodes = consumedNodes.Count;

        // ---- build one mesh per group ---------------------------------------------------------------
        var newNodeNames = new List<string>();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            string name = groups.Count == 1 ? options.Name : $"{options.Name}_{SafeSuffix(groupLabels[gi])}";
            var gr = BuildGroup(model, groups[gi], options, name, groupLabels[gi]);
            report.Groups.Add(gr);
            newNodeNames.Add(name);
        }

        // ---- detach consumed primitives from their source nodes -------------------------------------
        var consumedByNode = prims.GroupBy(p => p.Node).ToDictionary(g => g.Key, g => g.Select(p => p.Prim).ToHashSet());
        var leftoverCache = new Dictionary<string, Mesh>();
        var clearedNodes = new List<Node>();
        foreach (var (node, consumed) in consumedByNode)
        {
            var mesh = node.Mesh!;
            var leftover = mesh.Primitives.Where(p => !consumed.Contains(p)).ToList();
            if (leftover.Count == 0)
            {
                node.Mesh = null;
                node.Skin = null;
                clearedNodes.Add(node);
            }
            else
            {
                // keep the node with a new mesh holding only the primitives that were not joined
                string cacheKey = $"{mesh.LogicalIndex}:{string.Join(",", leftover.Select(p => p.LogicalIndex))}";
                if (!leftoverCache.TryGetValue(cacheKey, out var newMesh))
                {
                    newMesh = CloneMeshSubset(model, mesh, leftover);
                    leftoverCache[cacheKey] = newMesh;
                }
                node.Mesh = newMesh;
                warnings.Add($"'{Name(node)}': {leftover.Count} primitive(s) were not joined and stay on the node.");
            }
        }

        // ---- structural clean-up: drop emptied nodes, prune orphaned resources ------------------------
        var pkg = GlbPackage.FromModel(model);
        if (options.RemoveSources && clearedNodes.Count > 0)
        {
            var scope = new HashSet<int>();
            var stop = CommonAncestor(consumedNodes);
            if (stop != null && consumedNodes.Contains(stop)) stop = stop.VisualParent;
            foreach (var n in clearedNodes)
                for (var a = n; a != null && a != stop; a = a.VisualParent) scope.Add(a.LogicalIndex);
            var removable = CleanEmptyNodes.FindRemovable(model, new CleanEmptyNodesOptions { OnlyNodes = scope, FoldNonIdentityTransforms = false });
            GltfStructure.RemoveNodes(pkg, removable.Select(n => n.LogicalIndex).ToList(), foldTransforms: false);
        }
        var prune = GltfStructure.PruneUnused(pkg);
        report.PruneSummary = prune.ToString();

        var result = pkg.ToModel();
        for (int gi = 0; gi < report.Groups.Count; gi++)
            report.Groups[gi].NewNodeIndex = result.LogicalNodes.FirstOrDefault(n => n.Name == newNodeNames[gi] && n.Mesh != null)?.LogicalIndex ?? -1;
        return result;
    }

    // -------------------------------------------------------------------------------------------

    private static JoinGroupReport BuildGroup(ModelRoot model, List<SourcePrim> prims, JoinOptions options, string name, string label)
    {
        var gr = new JoinGroupReport { Label = label, NodeName = name };
        var warnings = gr.Warnings;

        var nodes = prims.Select(p => p.Node).Distinct().ToList();
        gr.SourceNodes = nodes.Count;
        var skins = nodes.Select(n => n.Skin).Distinct().ToList();
        if (skins.Count > 1) throw new InvalidOperationException($"Group '{label}' mixes skins – this should not happen.");
        var skin = skins[0];
        bool skinned = skin != null;

        // join parent: below the common ancestor of the group's sources; geometry is baked into its local space
        var parent = CommonAncestor(nodes);
        if (parent != null && nodes.Contains(parent)) parent = parent.VisualParent;
        Matrix4x4.Invert(parent?.WorldMatrix ?? Matrix4x4.Identity, out var parentInverse);

        // usages (materials) + uv transforms
        var usages = new List<MaterialUsage>();
        var usageIndex = new Dictionary<Material, int>();
        int defaultUsage = -1;
        foreach (var sp in prims)
        {
            sp.World = skinned ? Matrix4x4.Identity : sp.Node.WorldMatrix * parentInverse;
            if (sp.Prim.Material == null)
            {
                if (defaultUsage < 0) { defaultUsage = usages.Count; usages.Add(new MaterialUsage { Material = null }); }
                sp.UsageIndex = defaultUsage;
            }
            else if (!usageIndex.TryGetValue(sp.Prim.Material, out sp.UsageIndex))
            {
                sp.UsageIndex = usages.Count;
                usageIndex[sp.Prim.Material] = sp.UsageIndex;
                usages.Add(new MaterialUsage { Material = sp.Prim.Material });
            }
            sp.UvTransform = UvTransformOf(sp.Prim.Material, warnings);
        }

        // uv bounds per material (after texture transform and per-island tile normalisation)
        foreach (var sp in prims)
        {
            var uvAcc = sp.Prim.GetVertexAccessor("TEXCOORD_0");
            if (uvAcc == null) continue;
            var (allowU, allowV) = RepeatAxes(sp.Prim.Material);
            sp.Uvs = NormalizeIslands(uvAcc.AsVector2Array(), sp.Triangles, sp.UvTransform, allowU, allowV);
            var usage = usages[sp.UsageIndex];
            var used = new HashSet<int>();
            foreach (var (a, b, c) in sp.Triangles) { used.Add(a); used.Add(b); used.Add(c); }
            foreach (int i in used) usage.Include(sp.Uvs[i]);
        }

        // extensions the merged (core PBR) material cannot carry
        foreach (var u in usages)
        {
            if (u.Material == null) continue;
            var ext = JoinGrouping.ExtensionsOf(u.Material);
            var mname = string.IsNullOrEmpty(u.Material.Name) ? $"material #{u.Material.LogicalIndex}" : u.Material.Name;
            if (ext.Remove("KHR_materials_pbrSpecularGlossiness"))
                warnings.Add($"'{mname}': spec-gloss material approximated (diffuse → base colour, metallic 0, roughness = 1 − glossiness).");
            if (ext.Count > 0)
                warnings.Add($"'{mname}': {string.Join(", ", ext.OrderBy(x => x))} not carried over by the merged material.");
        }

        // atlas
        var atlas = MaterialAtlasBaker.Bake(usages, options.Atlas);
        warnings.AddRange(atlas.Warnings);
        gr.AtlasWidth = atlas.Width;
        gr.AtlasHeight = atlas.Height;
        gr.Channels.AddRange(atlas.Images.Keys.Select(k => k.ToString()));
        gr.UniqueCells = atlas.Cells.Distinct().Count();
        for (int i = 0; i < usages.Count; i++)
        {
            var c = atlas.Cells[i];
            var mname = usages[i].Material == null ? "<default>" : (string.IsNullOrEmpty(usages[i].Material!.Name) ? $"#{usages[i].Material!.LogicalIndex}" : usages[i].Material!.Name);
            gr.CellTable.Add($"{mname}: {c.Content.W}×{c.Content.H} @ ({c.Content.X},{c.Content.Y})" + (c.Solid ? " solid" : $" uv-range {c.RepeatsU:0.##}×{c.RepeatsV:0.##} tiles") + (c.Clamped ? " CLAMPED" : "") + (c.SharedBy > 1 ? $" (shared by {c.SharedBy})" : ""));
        }

        // geometry
        bool anyColor = prims.Any(p => p.Prim.GetVertexAccessor("COLOR_0") != null);
        bool allTangent = prims.All(p => p.Prim.GetVertexAccessor("TANGENT") != null);
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
            var srcUv = sp.Uvs;
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

                var uv = srcUv != null ? srcUv[i] : Vector2.Zero;
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

        gr.SourcePrimitives = prims.Count;
        gr.SourceMaterials = usages.Count;
        gr.Vertices = positions.Count;
        gr.Triangles = indices.Count / 3;

        // material + mesh
        var material = CreateMaterial(model, name, atlas);
        var mesh = model.CreateMesh(name);
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

        // node
        foreach (var n in nodes)
            for (var a = n; a != null && a != parent; a = a.VisualParent)
                if (a.IsTransformAnimated) { warnings.Add($"'{Name(a)}' is animated below the join parent; its animation is baked at rest pose into the joined mesh."); break; }

        var scene = model.DefaultScene ?? model.LogicalScenes.First();
        var newNode = parent != null ? parent.CreateNode(name) : scene.CreateNode(name);
        newNode.LocalMatrix = Matrix4x4.Identity;
        if (skinned) newNode.Skin = skin;
        newNode.Mesh = mesh;
        return gr;
    }

    /// <summary>New mesh containing only the given primitives of <paramref name="src"/> (accessors are shared, not copied).</summary>
    private static Mesh CloneMeshSubset(ModelRoot model, Mesh src, List<MeshPrimitive> keep)
    {
        var mesh = model.CreateMesh(src.Name);
        mesh.Extras = src.Extras?.DeepClone();
        foreach (var p in keep)
        {
            var np = mesh.CreatePrimitive();
            np.DrawPrimitiveType = p.DrawPrimitiveType;
            np.Material = p.Material;
            foreach (var kv in p.VertexAccessors) np.SetVertexAccessor(kv.Key, kv.Value);
            if (p.IndexAccessor != null) np.SetIndexAccessor(p.IndexAccessor);
            for (int t = 0; t < p.MorphTargetsCount; t++) np.SetMorphTargetAccessors(t, p.GetMorphTargetAccessors(t));
        }
        if (src.MorphWeights.Count > 0) mesh.SetMorphWeights(src.MorphWeights.ToArray());
        return mesh;
    }

    private static string SafeSuffix(string label)
    {
        var s = label.Replace(" · ", "_").Replace('/', '-').Replace("#", "").Replace(" ", "");
        return string.IsNullOrEmpty(s) ? "group" : s;
    }

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

    /// <summary>Per axis: may UVs be shifted by whole tiles? Only when every textured channel repeats on that axis.</summary>
    private static (bool U, bool V) RepeatAxes(Material? m)
    {
        if (m == null) return (true, true);
        bool u = true, v = true, any = false;
        foreach (var ch in m.Channels)
        {
            if (ch.Texture == null) continue;
            any = true;
            var s = ch.Texture.Sampler;
            if ((s?.WrapS ?? TextureWrapMode.REPEAT) != TextureWrapMode.REPEAT) u = false;
            if ((s?.WrapT ?? TextureWrapMode.REPEAT) != TextureWrapMode.REPEAT) v = false;
        }
        return any ? (u, v) : (true, true);
    }

    /// <summary>
    /// Applies the texture transform and then shifts every UV island (triangles connected through shared
    /// vertices) by whole tiles so it sits as close to [0,1] as possible. With REPEAT wrapping this is
    /// invisible, but it stops islands parked in neighbouring tiles from inflating the atlas cell.
    /// </summary>
    private static Vector2[] NormalizeIslands(IReadOnlyList<Vector2> src, List<(int A, int B, int C)> tris, Matrix3x2 xf, bool allowU, bool allowV)
    {
        var uv = new Vector2[src.Count];
        for (int i = 0; i < uv.Length; i++) uv[i] = Vector2.Transform(src[i], xf);
        if (!allowU && !allowV) return uv;

        var parent = new int[uv.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (a, b, c) in tris) { parent[Find(a)] = Find(b); parent[Find(b)] = Find(c); }

        var lo = new Dictionary<int, Vector2>();
        var hi = new Dictionary<int, Vector2>();
        foreach (var (a, b, c) in tris)
            foreach (int i in new[] { a, b, c })
            {
                int r = Find(i);
                lo[r] = lo.TryGetValue(r, out var l) ? Vector2.Min(l, uv[i]) : uv[i];
                hi[r] = hi.TryGetValue(r, out var h) ? Vector2.Max(h, uv[i]) : uv[i];
            }

        static float Shift(float l, float h)
        {
            // pick the whole-tile shift that leaves the least of [l,h] outside [0,1]
            float k0 = MathF.Floor(l), best = k0, bestOut = float.MaxValue;
            for (float k = k0; k <= k0 + 1; k++)
            {
                float outside = MathF.Max(0, -(l - k)) + MathF.Max(0, (h - k) - 1);
                if (outside < bestOut - 1e-6f) { bestOut = outside; best = k; }
            }
            return best;
        }

        var shift = new Dictionary<int, Vector2>();
        foreach (var (r, l) in lo)
        {
            var h = hi[r];
            shift[r] = new Vector2(allowU ? Shift(l.X, h.X) : 0f, allowV ? Shift(l.Y, h.Y) : 0f);
        }
        for (int i = 0; i < uv.Length; i++)
            if (shift.TryGetValue(Find(i), out var sh)) uv[i] -= sh;
        return uv;
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
