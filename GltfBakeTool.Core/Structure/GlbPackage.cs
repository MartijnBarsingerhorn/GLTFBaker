using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace GltfBakeTool.Core.Structure;

/// <summary>
/// A GLB split into its JSON document and binary chunk. Structural edits (node removal,
/// pruning, buffer rebuild) are done here on the raw glTF DOM so that everything we don't
/// touch survives byte-for-byte; SharpGLTF is used for parsing and for authoring new content.
/// </summary>
public sealed class GlbPackage
{
    private const uint Magic = 0x46546C67;   // "glTF"
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    public JsonObject Json { get; }
    public byte[] Bin { get; set; }

    public GlbPackage(JsonObject json, byte[] bin)
    {
        Json = json;
        Bin = bin;
    }

    public static GlbPackage FromModel(ModelRoot model)
    {
        // WriteGLB always produces a single buffer with everything embedded.
        var glb = model.WriteGLB().ToArray();
        return Parse(glb);
    }

    public static GlbPackage Parse(ReadOnlySpan<byte> glb)
    {
        if (glb.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != Magic)
            throw new InvalidDataException("Not a GLB container.");
        int total = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb[8..]);
        int pos = 12;
        JsonObject? json = null;
        byte[] bin = System.Array.Empty<byte>();
        while (pos + 8 <= total)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb[pos..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(glb[(pos + 4)..]);
            var data = glb.Slice(pos + 8, len);
            if (type == ChunkJson) json = JsonNode.Parse(data)!.AsObject();
            else if (type == ChunkBin) bin = data.ToArray();
            pos += 8 + len;
        }
        return new GlbPackage(json ?? throw new InvalidDataException("GLB has no JSON chunk."), bin);
    }

    public byte[] ToGlb()
    {
        // keep buffers[0].byteLength in sync
        var buffers = Json["buffers"]?.AsArray();
        if (buffers == null || buffers.Count == 0)
        {
            buffers = new JsonArray();
            Json["buffers"] = buffers;
            buffers.Add(new JsonObject());
        }
        buffers[0]!["byteLength"] = Bin.Length;
        buffers[0]!.AsObject().Remove("uri");

        var jsonBytes = Encoding.UTF8.GetBytes(Json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonPadded = Align4(jsonBytes.Length);
        int binPadded = Align4(Bin.Length);
        bool hasBin = Bin.Length > 0;

        int total = 12 + 8 + jsonPadded + (hasBin ? 8 + binPadded : 0);
        var outBytes = new byte[total];
        var span = outBytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        int p = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(span[p..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 4)..], ChunkJson);
        p += 8;
        jsonBytes.CopyTo(span[p..]);
        span.Slice(p + jsonBytes.Length, jsonPadded - jsonBytes.Length).Fill((byte)' ');
        p += jsonPadded;
        if (hasBin)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[p..], (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 4)..], ChunkBin);
            p += 8;
            Bin.CopyTo(span[p..]);
        }
        return outBytes;
    }

    /// <summary>Re-parses through SharpGLTF (with validation) so any structural mistake surfaces immediately.</summary>
    public ModelRoot ToModel() => ModelRoot.ParseGLB(ToGlb());

    private static int Align4(int n) => (n + 3) & ~3;

    // ---- small DOM helpers ---------------------------------------------------------------

    public JsonArray Array(string name) => Json[name]?.AsArray() ?? new JsonArray();

    public static int? GetInt(JsonNode? n) => n == null ? null : n.GetValue<int>();
}
