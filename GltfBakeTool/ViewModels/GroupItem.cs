using System.Windows.Media;
using GltfBakeTool.Core.Grouping;

namespace GltfBakeTool.ViewModels;

/// <summary>One material-compatibility group as shown in the "Join groups" panel.</summary>
public sealed class GroupItem
{
    public required JoinGroup Group { get; init; }
    public int Index => Group.Index;
    public string Label => Group.Label;
    public int MaterialCount => Group.Materials.Count;
    public int PrimitiveCount => Group.Primitives.Count;
    public int NodeCount => Group.Nodes.Count;
    public int MixedCount => Group.MixedNodes.Count;
    public string Counts => $"{MaterialCount} mat · {PrimitiveCount} prim · {NodeCount} node" + (MixedCount > 0 ? $" ({MixedCount} mixed)" : "");
    public string ToolTip => $"{Label}\nMaterials: {string.Join(", ", Group.Materials.Select(m => m == null ? "<default>" : (string.IsNullOrEmpty(m.Name) ? $"#{m.LogicalIndex}" : m.Name)))}"
                             + (MixedCount > 0 ? $"\nMixed nodes (primitives in several groups): {string.Join(", ", Group.MixedNodes.Select(n => n.Name))}" : "")
                             + "\nClick to check this group's nodes.";
    public required Color Color { get; init; }
    public Brush Brush => new SolidColorBrush(Color);

    /// <summary>Distinguishable palette for group badges / viewport tinting.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0x4E, 0x9A, 0xF1), // blue
        Color.FromRgb(0xF2, 0x8E, 0x2B), // orange
        Color.FromRgb(0x5C, 0xB8, 0x5C), // green
        Color.FromRgb(0xD6, 0x4B, 0x4B), // red
        Color.FromRgb(0xA0, 0x6C, 0xD5), // purple
        Color.FromRgb(0xE1, 0xC1, 0x3A), // yellow
        Color.FromRgb(0x2A, 0xB7, 0xB7), // teal
        Color.FromRgb(0xE0, 0x6F, 0xB4), // pink
        Color.FromRgb(0x8C, 0x6D, 0x3F), // brown
        Color.FromRgb(0x9E, 0x9E, 0x9E), // grey
    };

    public static Color ColorFor(int index) => Palette[index % Palette.Length];
}
