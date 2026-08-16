using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpGLTF.Schema2;

namespace GltfBakeTool.ViewModels;

/// <summary>Tree-view item wrapping a glTF <see cref="Node"/>.</summary>
public sealed partial class NodeItem : ObservableObject
{
    public Node Node { get; }
    public NodeItem? Parent { get; }
    public ObservableCollection<NodeItem> Children { get; } = new();

    public string Name => string.IsNullOrEmpty(Node.Name) ? $"<node {Node.LogicalIndex}>" : Node.Name;

    /// <summary>Short description of what the node carries.</summary>
    public string Kind { get; }

    /// <summary>True when the empty-node cleanup would remove this node.</summary>
    public bool IsRemovableEmpty { get; }

    public bool HasMesh => Node.Mesh != null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isChecked;

    /// <summary>One brush per join group this node's primitives belong to (empty: no mesh). Mixed nodes show several badges.</summary>
    public ObservableCollection<System.Windows.Media.Brush> GroupBrushes { get; } = new();
    [ObservableProperty] private string? _groupToolTip;

    public event Action<NodeItem>? CheckedChanged;

    public NodeItem(Node node, NodeItem? parent, HashSet<Node> protectedNodes, HashSet<Node> removable)
    {
        Node = node;
        Parent = parent;
        IsRemovableEmpty = removable.Contains(node);
        Kind = DescribeKind(node, protectedNodes);
        foreach (var c in node.VisualChildren)
            Children.Add(new NodeItem(c, this, protectedNodes, removable));
    }

    private static string DescribeKind(Node n, HashSet<Node> protectedNodes)
    {
        var parts = new List<string>();
        if (n.Mesh != null) parts.Add(n.Skin != null ? "skinned mesh" : "mesh");
        if (n.Camera != null) parts.Add("camera");
        if (n.PunctualLight != null) parts.Add("light");
        if (protectedNodes.Contains(n) && n.Mesh == null) parts.Add("joint/animated");
        if (parts.Count == 0) parts.Add(Core.Scene.NodeInfo.IsIdentity(n.LocalMatrix) ? "empty" : "transform");
        return string.Join(", ", parts);
    }

    partial void OnIsCheckedChanged(bool value)
    {
        foreach (var c in Children) c.IsChecked = value;
        CheckedChanged?.Invoke(this);
    }

    public IEnumerable<NodeItem> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}
