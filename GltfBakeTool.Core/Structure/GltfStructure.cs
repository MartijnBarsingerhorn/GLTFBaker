using System.Numerics;
using System.Text.Json.Nodes;

namespace GltfBakeTool.Core.Structure;

/// <summary>Structural glTF edits on the raw JSON DOM: node removal with re-parenting, pruning, buffer rebuild.</summary>
public static class GltfStructure
{
    // ---------------------------------------------------------------------------------------
    // Node removal
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Removes the given nodes. Their children are spliced into the parent's child list at the
    /// removed node's position (or become scene roots). When <paramref name="foldTransforms"/> is set,
    /// a removed node's local transform is composed into its children; otherwise it is dropped.
    /// Node references everywhere in the file are re-indexed.
    /// </summary>
    public static void RemoveNodes(GlbPackage pkg, IReadOnlyCollection<int> nodeIndices, bool foldTransforms)
    {
        if (nodeIndices.Count == 0) return;
        var nodes = pkg.Json["nodes"]!.AsArray();
        var scenes = pkg.Array("scenes");
        var remove = new HashSet<int>(nodeIndices);

        foreach (int r in remove)
        {
            var node = nodes[r]!.AsObject();
            var children = ReadIntArray(node["children"]);

            if (foldTransforms && children.Count > 0)
            {
                var local = ReadLocalMatrix(node);
                if (!IsIdentity(local))
                    foreach (int c in children)
                    {
                        var child = nodes[c]!.AsObject();
                        WriteLocalMatrix(child, ReadLocalMatrix(child) * local);
                    }
            }

            // splice children into every container that references r
            foreach (var n in nodes)
                SpliceChildren(n!.AsObject(), "children", r, children);
            foreach (var s in scenes)
                SpliceChildren(s!.AsObject(), "nodes", r, children);

            node.Remove("children");
        }

        // compact node array and re-index every node reference
        var map = BuildMap(nodes.Count, remove);
        var newNodes = new JsonArray();
        for (int i = 0; i < nodes.Count; i++)
            if (map[i] >= 0) newNodes.Add(nodes[i]!.DeepClone());
        pkg.Json["nodes"] = newNodes;

        foreach (var n in newNodes) RemapArray(n!.AsObject(), "children", map);
        foreach (var s in scenes) RemapArray(s!.AsObject(), "nodes", map);
        foreach (var sk in pkg.Array("skins"))
        {
            var skin = sk!.AsObject();
            RemapArray(skin, "joints", map, throwIfRemoved: "skin joint");
            RemapRef(skin, "skeleton", map, throwIfRemoved: "skin skeleton");
        }
        foreach (var an in pkg.Array("animations"))
            foreach (var ch in an!["channels"]?.AsArray() ?? new JsonArray())
                if (ch!["target"] is JsonObject target)
                    RemapRef(target, "node", map, throwIfRemoved: "animation target");
    }

    private static void SpliceChildren(JsonObject container, string key, int removed, List<int> replacement)
    {
        if (container[key] is not JsonArray arr) return;
        var list = ReadIntArray(arr);
        int idx = list.IndexOf(removed);
        if (idx < 0) return;
        list.RemoveAt(idx);
        // avoid duplicates if (malformed) file already lists a child twice
        var insert = replacement.Where(c => !list.Contains(c)).ToList();
        list.InsertRange(idx, insert);
        if (list.Count == 0) container.Remove(key);
        else container[key] = ToJsonArray(list);
    }

    // ---------------------------------------------------------------------------------------
    // Pruning
    // ---------------------------------------------------------------------------------------

    public sealed class PruneReport
    {
        public int Meshes, Skins, Cameras, Materials, Textures, Samplers, Images, Accessors, BufferViews;
        public long BytesBefore, BytesAfter;
        public override string ToString()
            => $"pruned {Meshes} meshes, {Materials} materials, {Textures} textures, {Images} images, {Accessors} accessors, {BufferViews} bufferViews; binary {BytesBefore:N0} → {BytesAfter:N0} bytes";
    }

    /// <summary>Removes meshes, skins, cameras, materials, textures, samplers, images, accessors and bufferViews not reachable from the scene nodes, then rebuilds the binary buffer.</summary>
    public static PruneReport PruneUnused(GlbPackage pkg)
    {
        var report = new PruneReport { BytesBefore = pkg.Bin.Length };
        var J = pkg.Json;

        var nodes = pkg.Array("nodes");
        var meshes = pkg.Array("meshes");
        var skins = pkg.Array("skins");
        var cameras = pkg.Array("cameras");
        var materials = pkg.Array("materials");
        var textures = pkg.Array("textures");
        var samplers = pkg.Array("samplers");
        var images = pkg.Array("images");
        var accessors = pkg.Array("accessors");
        var bufferViews = pkg.Array("bufferViews");

        var usedMesh = new HashSet<int>(); var usedSkin = new HashSet<int>(); var usedCam = new HashSet<int>();
        var usedMat = new HashSet<int>(); var usedTex = new HashSet<int>(); var usedSampler = new HashSet<int>();
        var usedImg = new HashSet<int>(); var usedAcc = new HashSet<int>(); var usedBv = new HashSet<int>();

        // nodes (all nodes – nodes themselves are never pruned here)
        foreach (var n0 in nodes)
        {
            var n = n0!.AsObject();
            AddRef(usedMesh, n["mesh"]);
            AddRef(usedSkin, n["skin"]);
            AddRef(usedCam, n["camera"]);
            if (n["extensions"]?["EXT_mesh_gpu_instancing"]?["attributes"] is JsonObject inst)
                foreach (var kv in inst) AddRef(usedAcc, kv.Value);
        }
        // meshes
        foreach (int mi in usedMesh)
        {
            foreach (var p0 in meshes[mi]!["primitives"]?.AsArray() ?? new JsonArray())
            {
                var p = p0!.AsObject();
                if (p["attributes"] is JsonObject attrs) foreach (var kv in attrs) AddRef(usedAcc, kv.Value);
                AddRef(usedAcc, p["indices"]);
                AddRef(usedMat, p["material"]);
                foreach (var t in p["targets"]?.AsArray() ?? new JsonArray())
                    foreach (var kv in t!.AsObject()) AddRef(usedAcc, kv.Value);
                AddRef(usedBv, p["extensions"]?["KHR_draco_mesh_compression"]?["bufferView"]);
                foreach (var m in p["extensions"]?["KHR_materials_variants"]?["mappings"]?.AsArray() ?? new JsonArray())
                    AddRef(usedMat, m!["material"]);
            }
        }
        // skins
        foreach (int si in usedSkin) AddRef(usedAcc, skins[si]!["inverseBindMatrices"]);
        // animations
        foreach (var a in pkg.Array("animations"))
            foreach (var s in a!["samplers"]?.AsArray() ?? new JsonArray())
            {
                AddRef(usedAcc, s!["input"]);
                AddRef(usedAcc, s!["output"]);
            }
        // materials → textures (any object property ending in "Texture" holding {"index":n}, incl. extensions)
        foreach (int mi in usedMat) CollectTextureRefs(materials[mi]!, usedTex);
        // textures → images, samplers
        foreach (int ti in usedTex)
        {
            var t = textures[ti]!.AsObject();
            AddRef(usedImg, t["source"]);
            AddRef(usedSampler, t["sampler"]);
            if (t["extensions"] is JsonObject exts)
                foreach (var kv in exts) AddRef(usedImg, kv.Value?["source"]);
        }
        // images → bufferViews
        foreach (int ii in usedImg) AddRef(usedBv, images[ii]!["bufferView"]);
        // accessors → bufferViews
        foreach (int ai in usedAcc)
        {
            var a = accessors[ai]!.AsObject();
            AddRef(usedBv, a["bufferView"]);
            AddRef(usedBv, a["sparse"]?["indices"]?["bufferView"]);
            AddRef(usedBv, a["sparse"]?["values"]?["bufferView"]);
        }

        // ---- compact arrays and remap references ------------------------------------------
        var meshMap = Compact(J, "meshes", usedMesh, out report.Meshes);
        var skinMap = Compact(J, "skins", usedSkin, out report.Skins);
        var camMap = Compact(J, "cameras", usedCam, out report.Cameras);
        var matMap = Compact(J, "materials", usedMat, out report.Materials);
        var texMap = Compact(J, "textures", usedTex, out report.Textures);
        var samplerMap = Compact(J, "samplers", usedSampler, out report.Samplers);
        var imgMap = Compact(J, "images", usedImg, out report.Images);
        var accMap = Compact(J, "accessors", usedAcc, out report.Accessors);
        var bvMap = Compact(J, "bufferViews", usedBv, out report.BufferViews);

        foreach (var n0 in pkg.Array("nodes"))
        {
            var n = n0!.AsObject();
            RemapRef(n, "mesh", meshMap); RemapRef(n, "skin", skinMap); RemapRef(n, "camera", camMap);
            if (n["extensions"]?["EXT_mesh_gpu_instancing"]?["attributes"] is JsonObject inst)
                RemapAllValues(inst, accMap);
        }
        foreach (var m0 in pkg.Array("meshes"))
            foreach (var p0 in m0!["primitives"]?.AsArray() ?? new JsonArray())
            {
                var p = p0!.AsObject();
                if (p["attributes"] is JsonObject attrs) RemapAllValues(attrs, accMap);
                RemapRef(p, "indices", accMap);
                RemapRef(p, "material", matMap);
                foreach (var t in p["targets"]?.AsArray() ?? new JsonArray()) RemapAllValues(t!.AsObject(), accMap);
                if (p["extensions"]?["KHR_draco_mesh_compression"] is JsonObject draco) RemapRef(draco, "bufferView", bvMap);
                foreach (var m in p["extensions"]?["KHR_materials_variants"]?["mappings"]?.AsArray() ?? new JsonArray())
                    RemapRef(m!.AsObject(), "material", matMap);
            }
        foreach (var s in pkg.Array("skins")) RemapRef(s!.AsObject(), "inverseBindMatrices", accMap);
        foreach (var a in pkg.Array("animations"))
            foreach (var s in a!["samplers"]?.AsArray() ?? new JsonArray())
            {
                RemapRef(s!.AsObject(), "input", accMap);
                RemapRef(s!.AsObject(), "output", accMap);
            }
        foreach (var m in pkg.Array("materials")) RemapTextureRefs(m!, texMap);
        foreach (var t0 in pkg.Array("textures"))
        {
            var t = t0!.AsObject();
            RemapRef(t, "source", imgMap);
            RemapRef(t, "sampler", samplerMap);
            if (t["extensions"] is JsonObject exts)
                foreach (var kv in exts) if (kv.Value is JsonObject e) RemapRef(e, "source", imgMap);
        }
        foreach (var i in pkg.Array("images")) RemapRef(i!.AsObject(), "bufferView", bvMap);
        foreach (var a0 in pkg.Array("accessors"))
        {
            var a = a0!.AsObject();
            RemapRef(a, "bufferView", bvMap);
            if (a["sparse"]?["indices"] is JsonObject si) RemapRef(si, "bufferView", bvMap);
            if (a["sparse"]?["values"] is JsonObject sv) RemapRef(sv, "bufferView", bvMap);
        }

        RebuildBuffer(pkg);
        report.BytesAfter = pkg.Bin.Length;
        return report;
    }

    /// <summary>Rewrites the binary chunk so it contains only the bytes referenced by the current bufferViews.</summary>
    public static void RebuildBuffer(GlbPackage pkg)
    {
        var buffers = pkg.Array("buffers");
        if (buffers.Count > 1) throw new NotSupportedException("Multiple buffers are not supported (expected a single GLB buffer).");

        var bufferViews = pkg.Array("bufferViews");
        var src = pkg.Bin;
        using var ms = new MemoryStream();
        foreach (var bv0 in bufferViews)
        {
            var bv = bv0!.AsObject();
            int offset = GlbPackage.GetInt(bv["byteOffset"]) ?? 0;
            int length = GlbPackage.GetInt(bv["byteLength"]) ?? 0;
            // align to 4
            while (ms.Length % 4 != 0) ms.WriteByte(0);
            bv["byteOffset"] = (int)ms.Length;
            bv["buffer"] = 0;
            ms.Write(src, offset, length);
        }
        while (ms.Length % 4 != 0) ms.WriteByte(0);
        pkg.Bin = ms.ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static void CollectTextureRefs(JsonNode node, HashSet<int> used)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value is JsonObject o && kv.Key.EndsWith("Texture", StringComparison.Ordinal) && o["index"] != null)
                    AddRef(used, o["index"]);
                if (kv.Value != null) CollectTextureRefs(kv.Value, used);
            }
        }
        else if (node is JsonArray arr)
            foreach (var e in arr) if (e != null) CollectTextureRefs(e, used);
    }

    private static void RemapTextureRefs(JsonNode node, int[] map)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                if (kv.Value is JsonObject o && kv.Key.EndsWith("Texture", StringComparison.Ordinal) && o["index"] != null)
                    RemapRef(o, "index", map);
                if (kv.Value != null) RemapTextureRefs(kv.Value, map);
            }
        }
        else if (node is JsonArray arr)
            foreach (var e in arr) if (e != null) RemapTextureRefs(e, map);
    }

    private static int[] Compact(JsonObject root, string arrayName, HashSet<int> used, out int removedCount)
    {
        removedCount = 0;
        if (root[arrayName] is not JsonArray arr) return Array.Empty<int>();
        var map = new int[arr.Count];
        var result = new JsonArray();
        for (int i = 0; i < arr.Count; i++)
        {
            if (used.Contains(i)) { map[i] = result.Count; result.Add(arr[i]!.DeepClone()); }
            else { map[i] = -1; removedCount++; }
        }
        if (result.Count == 0) root.Remove(arrayName);
        else root[arrayName] = result;
        return map;
    }

    private static int[] BuildMap(int count, HashSet<int> removed)
    {
        var map = new int[count];
        int next = 0;
        for (int i = 0; i < count; i++) map[i] = removed.Contains(i) ? -1 : next++;
        return map;
    }

    private static void AddRef(HashSet<int> set, JsonNode? n)
    {
        if (n != null) set.Add(n.GetValue<int>());
    }

    private static void RemapRef(JsonObject obj, string key, int[] map, string? throwIfRemoved = null)
    {
        if (obj[key] is not JsonNode n) return;
        int old = n.GetValue<int>();
        int nu = old < map.Length ? map[old] : -1;
        if (nu < 0)
        {
            if (throwIfRemoved != null) throw new InvalidOperationException($"Cannot remove node {old}: still referenced as {throwIfRemoved}.");
            obj.Remove(key);
        }
        else obj[key] = nu;
    }

    private static void RemapArray(JsonObject obj, string key, int[] map, string? throwIfRemoved = null)
    {
        if (obj[key] is not JsonArray arr) return;
        var list = new List<int>();
        foreach (var e in arr)
        {
            int old = e!.GetValue<int>();
            int nu = old < map.Length ? map[old] : -1;
            if (nu < 0)
            {
                if (throwIfRemoved != null) throw new InvalidOperationException($"Cannot remove node {old}: still referenced as {throwIfRemoved}.");
                continue;
            }
            list.Add(nu);
        }
        if (list.Count == 0) obj.Remove(key);
        else obj[key] = ToJsonArray(list);
    }

    private static void RemapAllValues(JsonObject obj, int[] map)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList()) RemapRef(obj, key, map);
    }

    private static List<int> ReadIntArray(JsonNode? n)
    {
        var list = new List<int>();
        if (n is JsonArray arr) foreach (var e in arr) list.Add(e!.GetValue<int>());
        return list;
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    // ---- transforms ----------------------------------------------------------------------

    public static Matrix4x4 ReadLocalMatrix(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            var f = m.Select(x => x!.GetValue<float>()).ToArray();
            // glTF is column-major; System.Numerics memory layout (row-vector convention) matches element-for-element.
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        var t = ReadVec(node["translation"], Vector3.Zero);
        var s = ReadVec(node["scale"], Vector3.One);
        var r = Quaternion.Identity;
        if (node["rotation"] is JsonArray q && q.Count == 4)
            r = new Quaternion(q[0]!.GetValue<float>(), q[1]!.GetValue<float>(), q[2]!.GetValue<float>(), q[3]!.GetValue<float>());
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    public static void WriteLocalMatrix(JsonObject node, Matrix4x4 m)
    {
        node.Remove("matrix"); node.Remove("translation"); node.Remove("rotation"); node.Remove("scale");
        if (Matrix4x4.Decompose(m, out var s, out var r, out var t))
        {
            if (t != Vector3.Zero) node["translation"] = new JsonArray(t.X, t.Y, t.Z);
            if (r != Quaternion.Identity) node["rotation"] = new JsonArray(r.X, r.Y, r.Z, r.W);
            if (s != Vector3.One) node["scale"] = new JsonArray(s.X, s.Y, s.Z);
        }
        else
        {
            node["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
        }
    }

    private static Vector3 ReadVec(JsonNode? n, Vector3 def)
        => n is JsonArray a && a.Count == 3 ? new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : def;

    public static bool IsIdentity(Matrix4x4 m, float eps = 1e-5f) => Scene.NodeInfo.IsIdentity(m, eps);
}
