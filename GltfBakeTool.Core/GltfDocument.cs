using SharpGLTF.Schema2;

namespace GltfBakeTool.Core;

/// <summary>
/// Owns a loaded glTF model. All operations produce a new <see cref="ModelRoot"/>;
/// snapshots (GLB byte arrays) drive undo.
/// </summary>
public sealed class GltfDocument
{
    public ModelRoot Model { get; private set; }
    public string? FilePath { get; private set; }

    private GltfDocument(ModelRoot model, string? path)
    {
        Model = model;
        FilePath = path;
    }

    public static GltfDocument Load(string path)
    {
        var model = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        return new GltfDocument(model, path);
    }

    public static GltfDocument FromModel(ModelRoot model, string? path = null) => new(model, path);

    public void Save(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".gltf") Model.SaveGLTF(path);
        else Model.SaveGLB(path);
        FilePath = path;
    }

    /// <summary>Serialises the current model to an in-memory GLB (used for undo snapshots).</summary>
    public byte[] Snapshot() => Model.WriteGLB().ToArray();

    public void Restore(byte[] glb)
    {
        Model = ModelRoot.ParseGLB(glb);
    }

    public void Replace(ModelRoot model) => Model = model;
}
