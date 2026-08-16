using GltfBakeTool.Core;
using GltfBakeTool.Core.Operations;
using GltfBakeTool.Core.Scene;
using SharpGLTF.Schema2;

if (args.Length < 2)
{
    Console.WriteLine("usage: cli info <file> | cli clean <in> <out> [--fold] | cli roundtrip <in> <out> | cli prune <in> <out>");
    return 1;
}

switch (args[0])
{
    case "info":
    {
        var doc = GltfDocument.Load(args[1]);
        PrintInfo(doc.Model);
        return 0;
    }
    case "roundtrip":
    {
        var doc = GltfDocument.Load(args[1]);
        var pkg = GltfBakeTool.Core.Structure.GlbPackage.FromModel(doc.Model);
        var result = pkg.ToModel();
        Console.WriteLine("--- before"); PrintInfo(doc.Model);
        Console.WriteLine("--- after"); PrintInfo(result);
        result.SaveGLB(args[2]);
        return 0;
    }
    case "prune":
    {
        var doc = GltfDocument.Load(args[1]);
        var pkg = GltfBakeTool.Core.Structure.GlbPackage.FromModel(doc.Model);
        var rep = GltfBakeTool.Core.Structure.GltfStructure.PruneUnused(pkg);
        Console.WriteLine(rep);
        var result = pkg.ToModel();
        PrintInfo(result);
        result.SaveGLB(args[2]);
        return 0;
    }
    case "clean":
    {
        var doc = GltfDocument.Load(args[1]);
        var opts = new CleanEmptyNodesOptions { FoldNonIdentityTransforms = args.Contains("--fold") };
        var result = CleanEmptyNodes.Run(doc.Model, opts, out var report);
        Console.WriteLine(report);
        foreach (var r in report.Removed) Console.WriteLine("  - " + r);
        foreach (var w in report.Warnings) Console.WriteLine("  ! " + w);
        Console.WriteLine("--- after"); PrintInfo(result);
        result.SaveGLB(args[2]);
        return 0;
    }
    case "join":
    {
        // cli join <in> <out> [--nodes 1,2,3] [--atlas 2048] [--jpeg] [--alpha auto|opaque|mask|blend] [--per-group [--tiling]]
        var doc = GltfDocument.Load(args[1]);
        var scene = doc.Model.DefaultScene ?? doc.Model.LogicalScenes.First();
        List<int> nodes;
        int ni = Array.IndexOf(args, "--nodes");
        if (ni >= 0) nodes = args[ni + 1].Split(',').Select(int.Parse).ToList();
        else nodes = scene.VisualChildren.Select(n => n.LogicalIndex).ToList();
        int ai = Array.IndexOf(args, "--atlas");
        int al = Array.IndexOf(args, "--alpha");
        var opts = new JoinOptions
        {
            Atlas = new GltfBakeTool.Core.Atlas.AtlasOptions
            {
                MaxAtlasSize = ai >= 0 ? int.Parse(args[ai + 1]) : 4096,
                JpegForColor = args.Contains("--jpeg"),
                Alpha = al >= 0 ? Enum.Parse<GltfBakeTool.Core.Atlas.AlphaPolicy>(args[al + 1], ignoreCase: true) : GltfBakeTool.Core.Atlas.AlphaPolicy.Auto,
            },
            Grouping = args.Contains("--per-group") ? new GltfBakeTool.Core.Grouping.GroupCriteria { SplitHighTiling = args.Contains("--tiling") } : null,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = JoinMeshes.Run(doc.Model, nodes, opts, out var report);
        Console.WriteLine($"{report} in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine(report.PruneSummary);
        foreach (var w in report.Warnings) Console.WriteLine("  ! " + w);
        foreach (var g in report.Groups)
        {
            Console.WriteLine("  group " + g);
            foreach (var w in g.Warnings) Console.WriteLine("    ! " + w);
            foreach (var c in g.CellTable) Console.WriteLine("    cell " + c);
        }
        Console.WriteLine("--- after"); PrintInfo(result);
        result.SaveGLB(args[2]);
        return 0;
    }
    case "groups":
    {
        // cli groups <file> [--tiling]
        var doc = GltfDocument.Load(args[1]);
        var crit = new GltfBakeTool.Core.Grouping.GroupCriteria { SplitHighTiling = args.Contains("--tiling") };
        var groups = GltfBakeTool.Core.Grouping.JoinGrouping.Compute(doc.Model, crit);
        foreach (var g in groups)
        {
            Console.WriteLine($"[{g.Index}] {g}");
            foreach (var m in g.Materials) Console.WriteLine($"      material {(m == null ? "<default>" : $"#{m.LogicalIndex} '{m.Name}'")}  ext=[{string.Join(",", GltfBakeTool.Core.Grouping.JoinGrouping.ExtensionsOf(m))}]");
            if (g.MixedNodes.Count > 0) Console.WriteLine($"      mixed nodes: {string.Join(", ", g.MixedNodes.Select(n => n.Name))}");
        }
        return 0;
    }
    case "materials":
    {
        var doc = GltfDocument.Load(args[1]);
        foreach (var m in doc.Model.LogicalMaterials)
        {
            Console.WriteLine($"#{m.LogicalIndex} '{m.Name}' alpha={m.Alpha} doubleSided={m.DoubleSided} unlit={m.Unlit}");
            foreach (var ch in m.Channels)
            {
                var tex = ch.Texture;
                var img = tex?.PrimaryImage;
                string pars = string.Join(", ", ch.Parameters.Select(p => $"{p.Name}={p.Value}"));
                string t = tex == null ? "no texture" : $"tex#{tex.LogicalIndex} img#{img?.LogicalIndex} {img?.Content.MimeType} uv{ch.TextureCoordinate} wrap={tex.Sampler?.WrapS}/{tex.Sampler?.WrapT}" + (ch.TextureTransform != null ? $" xform(off={ch.TextureTransform.Offset} scale={ch.TextureTransform.Scale} rot={ch.TextureTransform.Rotation})" : "");
                Console.WriteLine($"    {ch.Key,-20} {t}  [{pars}]");
            }
        }
        return 0;
    }
    case "dump-images":
    {
        var doc = GltfDocument.Load(args[1]);
        Directory.CreateDirectory(args[2]);
        foreach (var img in doc.Model.LogicalImages)
        {
            var ext = img.Content.IsPng ? ".png" : img.Content.IsJpg ? ".jpg" : ".bin";
            var path = Path.Combine(args[2], $"image_{img.LogicalIndex}{ext}");
            File.WriteAllBytes(path, img.Content.Content.ToArray());
            Console.WriteLine(path);
        }
        return 0;
    }
    default:
        Console.WriteLine("unknown command"); return 1;
}

static void PrintInfo(ModelRoot m)
{
    int prims = m.LogicalMeshes.Sum(x => x.Primitives.Count);
    Console.WriteLine($"{m.LogicalNodes.Count} nodes, {m.LogicalMeshes.Count} meshes, {prims} prims, {m.LogicalMaterials.Count} materials, {m.LogicalTextures.Count} textures, {m.LogicalImages.Count} images, {m.LogicalAnimations.Count} anims, {m.LogicalSkins.Count} skins, {m.LogicalAccessors.Count} accessors, extensions: [{string.Join(",", m.ExtensionsUsed)}]");
    var prot = NodeInfo.CollectProtectedNodes(m);
    var scene = m.DefaultScene ?? m.LogicalScenes.FirstOrDefault();
    if (scene == null) return;
    foreach (var root in scene.VisualChildren) Print(root, 0, prot);
}

static void Print(Node n, int depth, HashSet<Node> prot)
{
    var kind = n.Mesh != null ? (n.Skin != null ? "skinned" : "mesh") : n.Camera != null ? "camera" : n.PunctualLight != null ? "light" : prot.Contains(n) ? "joint/anim" : NodeInfo.IsIdentity(n.LocalMatrix) ? "EMPTY" : "xform";
    Console.WriteLine($"{new string(' ', depth * 2)}{n.Name ?? "<unnamed>"} [{kind}]{(NodeInfo.IsRemovableEmpty(n, prot) ? " *removable*" : "")}");
    foreach (var c in n.VisualChildren) Print(c, depth + 1, prot);
}
