using System.Numerics;
using GltfBakeTool.Core.Scene;
using GltfBakeTool.Core.Structure;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Operations;

public sealed record CleanEmptyNodesOptions
{
    /// <summary>Also remove empties whose transform is not identity, folding it into their children.</summary>
    public bool FoldNonIdentityTransforms { get; init; } = false;

    /// <summary>Keep nodes that carry glTF <c>extras</c> (custom metadata).</summary>
    public bool KeepNodesWithExtras { get; init; } = true;

    /// <summary>Restrict cleanup to these logical node indices; null = whole file.</summary>
    public IReadOnlySet<int>? OnlyNodes { get; init; }

    public float IdentityEpsilon { get; init; } = NodeInfo.IdentityEpsilon;
}

public sealed class CleanEmptyNodesReport
{
    public List<string> Removed { get; } = new();
    public List<string> Warnings { get; } = new();
    public int NodesBefore { get; set; }
    public int NodesAfter { get; set; }
    public override string ToString()
        => $"removed {Removed.Count} empty node(s): {NodesBefore} → {NodesAfter} nodes"
         + (Warnings.Count > 0 ? $" ({Warnings.Count} warning(s))" : "");
}

/// <summary>Removes "useless" container nodes: no content, not animated, not a joint, identity transform (or leaf).</summary>
public static class CleanEmptyNodes
{
    public static ModelRoot Run(ModelRoot model, CleanEmptyNodesOptions options, out CleanEmptyNodesReport report)
    {
        report = new CleanEmptyNodesReport { NodesBefore = model.LogicalNodes.Count };

        var toRemove = FindRemovable(model, options, report.Warnings);
        foreach (var n in toRemove) report.Removed.Add(NodePath.Of(n));

        if (toRemove.Count == 0)
        {
            report.NodesAfter = report.NodesBefore;
            return model;
        }

        var pkg = GlbPackage.FromModel(model);
        GltfStructure.RemoveNodes(pkg, toRemove.Select(n => n.LogicalIndex).ToList(), options.FoldNonIdentityTransforms);
        var result = pkg.ToModel();
        report.NodesAfter = result.LogicalNodes.Count;
        return result;
    }

    /// <summary>Nodes the cleanup would remove (used both by <see cref="Run"/> and by the UI preview).</summary>
    public static List<Node> FindRemovable(ModelRoot model, CleanEmptyNodesOptions options, List<string>? warnings = null)
    {
        var protectedNodes = NodeInfo.CollectProtectedNodes(model);
        var removed = new HashSet<Node>();
        var result = new List<Node>();

        // Post-order. A node whose *entire subtree* is removed becomes a leaf, and leaves are
        // removable regardless of their transform. (Removed children with surviving descendants
        // do not make a leaf: those descendants get spliced up into this node.)
        // Returns true when anything in the subtree (including the node itself) survives.
        bool Visit(Node node)
        {
            bool survivors = false;
            foreach (var c in node.VisualChildren) survivors |= Visit(c);
            var effectiveChildren = node.VisualChildren.Where(c => !removed.Contains(c)).ToList();
            bool subtreeGone = !survivors;

            if (options.OnlyNodes != null && !options.OnlyNodes.Contains(node.LogicalIndex)) return true;
            if (IsRemovable(node, protectedNodes, subtreeGone, effectiveChildren, options, out var warning))
            {
                removed.Add(node);
                result.Add(node);
                return survivors;
            }
            if (warning != null) warnings?.Add(warning);
            return true;
        }

        foreach (var scene in model.LogicalScenes)
            foreach (var root in scene.VisualChildren) Visit(root);
        return result;
    }

    public static bool IsRemovable(Node node, HashSet<Node> protectedNodes, bool subtreeGone, IReadOnlyList<Node> survivingChildren, CleanEmptyNodesOptions options, out string? warning)
    {
        warning = null;
        if (NodeInfo.HasContent(node)) return false;
        if (protectedNodes.Contains(node)) return false;
        if (options.KeepNodesWithExtras && node.Extras != null) return false;

        if (subtreeGone) return true;                               // (effective) leaf: transform is irrelevant
        if (NodeInfo.IsIdentity(node.LocalMatrix, options.IdentityEpsilon)) return true;
        if (!options.FoldNonIdentityTransforms) return false;

        // Folding is only safe when no surviving child animates its transform (its curves are
        // authored in the child's local space). Children of removed children will be spliced in
        // here as well, so check the whole surviving frontier.
        foreach (var child in SurvivingFrontier(node, survivingChildren))
        {
            if (child.IsTransformAnimated)
            {
                warning = $"'{Name(node)}' kept: child '{Name(child)}' is animated, transform cannot be folded.";
                return false;
            }
        }
        return true;
    }

    /// <summary>The nodes that will be this node's children after removed descendants are spliced out.</summary>
    private static IEnumerable<Node> SurvivingFrontier(Node node, IReadOnlyList<Node> survivingChildren)
    {
        // survivingChildren are direct children that stay; removed children contribute their own frontier,
        // which we approximate by all descendants that carry content or are protected – good enough for
        // the animation check (animated nodes are protected and therefore always in the frontier).
        foreach (var c in survivingChildren) yield return c;
        foreach (var c in node.VisualChildren)
            if (!survivingChildren.Contains(c))
                foreach (var d in Scene.GeometryExtractor.Flatten(c).Skip(1))
                    if (d.IsTransformAnimated) yield return d;
    }

    private static string Name(Node n) => string.IsNullOrEmpty(n.Name) ? $"<node {n.LogicalIndex}>" : n.Name;
}

/// <summary>Human-readable identity for a node: names along the path.</summary>
public static class NodePath
{
    public static string Of(Node node)
    {
        var parts = new Stack<string>();
        for (var n = node; n != null; n = n.VisualParent)
            parts.Push(string.IsNullOrEmpty(n.Name) ? $"<node {n.LogicalIndex}>" : n.Name);
        return string.Join("/", parts);
    }
}
