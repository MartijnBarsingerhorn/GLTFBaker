using System.Numerics;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Scene;

/// <summary>Facts about a node that the UI and the cleanup pass both need.</summary>
public static class NodeInfo
{
    public const float IdentityEpsilon = 1e-5f;

    public static bool HasContent(Node node)
        => node.Mesh != null || node.Camera != null || node.Skin != null || node.PunctualLight != null;

    public static bool IsIdentity(Matrix4x4 m, float eps = IdentityEpsilon)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) < eps && MathF.Abs(d.M12) < eps && MathF.Abs(d.M13) < eps && MathF.Abs(d.M14) < eps
            && MathF.Abs(d.M21) < eps && MathF.Abs(d.M22) < eps && MathF.Abs(d.M23) < eps && MathF.Abs(d.M24) < eps
            && MathF.Abs(d.M31) < eps && MathF.Abs(d.M32) < eps && MathF.Abs(d.M33) < eps && MathF.Abs(d.M34) < eps
            && MathF.Abs(d.M41) < eps && MathF.Abs(d.M42) < eps && MathF.Abs(d.M43) < eps && MathF.Abs(d.M44) < eps;
    }

    /// <summary>Nodes referenced by animation channels or by any skin (joints + skeleton roots).</summary>
    public static HashSet<Node> CollectProtectedNodes(ModelRoot model)
    {
        var set = new HashSet<Node>();
        foreach (var anim in model.LogicalAnimations)
            foreach (var ch in anim.Channels)
                if (ch.TargetNode != null) set.Add(ch.TargetNode);

        foreach (var skin in model.LogicalSkins)
        {
            if (skin.Skeleton != null) set.Add(skin.Skeleton);
            for (int i = 0; i < skin.JointsCount; i++)
                set.Add(skin.GetJoint(i).Joint);
        }
        return set;
    }

    /// <summary>
    /// A node is an "empty container" when it carries nothing, is not animated/skinned,
    /// and its local transform is identity.
    /// </summary>
    public static bool IsRemovableEmpty(Node node, HashSet<Node> protectedNodes)
        => !HasContent(node)
        && !protectedNodes.Contains(node)
        && node.Extras is null
        && IsIdentity(node.LocalMatrix);
}
