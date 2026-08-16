using System.Numerics;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Scene;

/// <summary>Plain-array view of one primitive, world-space, triangles only. Used by the viewer.</summary>
public sealed class PrimitiveGeometry
{
    public required Node Node { get; init; }
    public required MeshPrimitive Primitive { get; init; }
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector2[]? TexCoords { get; init; }
    public required int[] Indices { get; init; }
    public Material? Material => Primitive.Material;
}

public static class GeometryExtractor
{
    /// <summary>All triangle primitives in the default scene, transformed to world space (skinned meshes use bind pose = identity).</summary>
    public static IEnumerable<PrimitiveGeometry> ExtractScene(ModelRoot model)
    {
        var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault();
        if (scene == null) yield break;

        foreach (var node in scene.VisualChildren.SelectMany(Flatten))
        {
            if (node.Mesh == null) continue;
            foreach (var g in Extract(node)) yield return g;
        }
    }

    public static IEnumerable<Node> Flatten(Node node)
    {
        yield return node;
        foreach (var c in node.VisualChildren)
            foreach (var d in Flatten(c)) yield return d;
    }

    public static IEnumerable<PrimitiveGeometry> Extract(Node node)
    {
        if (node.Mesh == null) yield break;
        // glTF spec: node transform must be ignored for skinned meshes.
        var world = node.Skin != null ? Matrix4x4.Identity : node.WorldMatrix;

        foreach (var prim in node.Mesh.Primitives)
        {
            var g = Extract(node, prim, world);
            if (g != null) yield return g;
        }
    }

    public static PrimitiveGeometry? Extract(Node node, MeshPrimitive prim, Matrix4x4 world)
    {
        var posAcc = prim.GetVertexAccessor("POSITION");
        if (posAcc == null) return null;

        var tris = prim.GetTriangleIndices().ToList();
        if (tris.Count == 0) return null;

        var src = posAcc.AsVector3Array();
        var positions = new Vector3[src.Count];
        for (int i = 0; i < src.Count; i++) positions[i] = Vector3.Transform(src[i], world);

        Vector3[]? normals = null;
        var nrmAcc = prim.GetVertexAccessor("NORMAL");
        if (nrmAcc != null)
        {
            var n = nrmAcc.AsVector3Array();
            normals = new Vector3[n.Count];
            Matrix4x4.Invert(world, out var inv);
            var nm = Matrix4x4.Transpose(inv);
            for (int i = 0; i < n.Count; i++) normals[i] = Vector3.Normalize(Vector3.TransformNormal(n[i], nm));
        }

        Vector2[]? uvs = null;
        var uvAcc = prim.GetVertexAccessor("TEXCOORD_0");
        if (uvAcc != null)
        {
            var t = uvAcc.AsVector2Array();
            uvs = new Vector2[t.Count];
            for (int i = 0; i < t.Count; i++) uvs[i] = t[i];
        }

        bool flip = world.GetDeterminant() < 0;
        var indices = new int[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            indices[i * 3 + 0] = a;
            indices[i * 3 + 1] = flip ? c : b;
            indices[i * 3 + 2] = flip ? b : c;
        }

        return new PrimitiveGeometry
        {
            Node = node,
            Primitive = prim,
            Positions = positions,
            Normals = normals,
            TexCoords = uvs,
            Indices = indices,
        };
    }
}
