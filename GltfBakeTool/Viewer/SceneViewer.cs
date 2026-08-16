using System.IO;
using System.Windows.Media.Media3D;
using GltfBakeTool.Core.Scene;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpGLTF.Schema2;
using Material = SharpGLTF.Schema2.Material;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using HxMaterial = HelixToolkit.Wpf.SharpDX.Material;

namespace GltfBakeTool.Viewer;

/// <summary>Builds Helix scene elements from a glTF model and manages highlight state.</summary>
public sealed class SceneViewer
{
    private readonly Viewport3DX _viewport;
    private readonly GroupModel3D _root = new();
    private readonly Dictionary<Node, List<MeshGeometryModel3D>> _byNode = new();
    private readonly Dictionary<MeshGeometryModel3D, HxMaterial> _baseMaterial = new();
    private readonly Dictionary<Material, HxMaterial> _materialCache = new();

    private static readonly Color4 SelectedTint = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Color4 CheckedTint = new(0.45f, 0.75f, 1f, 1f);
    private readonly Dictionary<(HxMaterial, Color4), HxMaterial> _tintCache = new();

    public SceneViewer(Viewport3DX viewport)
    {
        _viewport = viewport;
        _viewport.EffectsManager = new DefaultEffectsManager();
        _viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
        {
            Position = new Point3D(3, 3, 3),
            LookDirection = new Vector3D(-3, -3, -3),
            UpDirection = new Vector3D(0, 1, 0),
            FarPlaneDistance = 10000,
            NearPlaneDistance = 0.01,
        };
        _viewport.Items.Add(new AmbientLight3D { Color = System.Windows.Media.Color.FromRgb(70, 70, 70) });
        _viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(-1, -1.5, -1), Color = System.Windows.Media.Color.FromRgb(200, 200, 200) });
        _viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(1, 0.5, 1), Color = System.Windows.Media.Color.FromRgb(90, 90, 100) });
        _viewport.Items.Add(_root);
    }

    public void Clear()
    {
        _root.Children.Clear();
        _byNode.Clear();
        _baseMaterial.Clear();
        _materialCache.Clear();
        _tintCache.Clear();
    }

    public void Load(ModelRoot? model)
    {
        Clear();
        if (model == null) return;

        var bmin = new System.Numerics.Vector3(float.MaxValue);
        var bmax = new System.Numerics.Vector3(float.MinValue);
        foreach (var g in GeometryExtractor.ExtractScene(model))
        {
            foreach (var p in g.Positions)
            {
                bmin = System.Numerics.Vector3.Min(bmin, p);
                bmax = System.Numerics.Vector3.Max(bmax, p);
            }
            var geom = new MeshGeometry3D
            {
                Positions = new Vector3Collection(g.Positions),
                Indices = new IntCollection(g.Indices),
            };
            if (g.Normals != null) geom.Normals = new Vector3Collection(g.Normals);
            else geom.UpdateNormals();
            if (g.TexCoords != null) geom.TextureCoordinates = new Vector2Collection(g.TexCoords);

            var mat = GetMaterial(g.Material);
            var element = new MeshGeometryModel3D
            {
                Geometry = geom,
                Material = mat,
                CullMode = g.Material?.DoubleSided == true ? SharpDX.Direct3D11.CullMode.None : SharpDX.Direct3D11.CullMode.Back,
                Tag = g,
            };
            _baseMaterial[element] = mat;
            if (!_byNode.TryGetValue(g.Node, out var list)) _byNode[g.Node] = list = new();
            list.Add(element);
            _root.Children.Add(element);
        }
        if (bmin.X <= bmax.X) FrameBounds(bmin, bmax);
    }

    /// <summary>Places the camera so the given bounds fill the view (independent of Helix's render state).</summary>
    private void FrameBounds(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        var center = (min + max) * 0.5f;
        float radius = Math.Max((max - min).Length() * 0.5f, 1e-3f);
        var dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1f, 0.7f, 1f));
        var pos = center + dir * radius * 2.2f;
        _viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
        {
            Position = new Point3D(pos.X, pos.Y, pos.Z),
            LookDirection = new Vector3D(center.X - pos.X, center.Y - pos.Y, center.Z - pos.Z),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = radius * 0.002,
            FarPlaneDistance = radius * 200,
            FieldOfView = 45,
        };
    }

    public void ZoomExtents() => _viewport.ZoomExtents(300);

    public void SetHighlights(Node? selected, IReadOnlyCollection<Node> checkedNodes)
    {
        var checkedSet = new HashSet<Node>(checkedNodes);
        foreach (var (node, list) in _byNode)
        {
            Color4? tint = null;
            if (selected != null && IsSelfOrDescendant(node, selected)) tint = SelectedTint;
            else if (checkedSet.Contains(node)) tint = CheckedTint;
            foreach (var el in list) el.Material = tint is { } t ? Tinted(_baseMaterial[el], t) : _baseMaterial[el];
        }
    }

    /// <summary>Same textures as the base material, multiplied by a highlight colour.</summary>
    private HxMaterial Tinted(HxMaterial baseMat, Color4 tint)
    {
        if (_tintCache.TryGetValue((baseMat, tint), out var cached)) return cached;
        HxMaterial result = baseMat is PhongMaterial p
            ? new PhongMaterial
            {
                DiffuseColor = new Color4(p.DiffuseColor.Red * tint.Red, p.DiffuseColor.Green * tint.Green, p.DiffuseColor.Blue * tint.Blue, p.DiffuseColor.Alpha),
                DiffuseMap = p.DiffuseMap,
                SpecularColor = p.SpecularColor,
                SpecularShininess = p.SpecularShininess,
                EmissiveColor = new Color4(tint.Red * 0.25f, tint.Green * 0.25f, tint.Blue * 0.25f, 1f),
            }
            : new PhongMaterial { DiffuseColor = tint };
        _tintCache[(baseMat, tint)] = result;
        return result;
    }

    private static bool IsSelfOrDescendant(Node node, Node ancestor)
    {
        for (var n = node; n != null; n = n.VisualParent)
            if (n == ancestor) return true;
        return false;
    }

    private HxMaterial GetMaterial(Material? m)
    {
        if (m == null) return new PhongMaterial { DiffuseColor = new Color4(0.8f, 0.8f, 0.8f, 1f) };
        if (_materialCache.TryGetValue(m, out var cached)) return cached;

        var phong = new PhongMaterial
        {
            SpecularColor = new Color4(0.15f, 0.15f, 0.15f, 1f),
            SpecularShininess = 20,
            DiffuseColor = new Color4(1, 1, 1, 1),
        };

        var baseColor = m.FindChannel("BaseColor") ?? m.FindChannel("Diffuse");
        if (baseColor is { } ch)
        {
            var c = ch.Color;
            phong.DiffuseColor = new Color4(c.X, c.Y, c.Z, c.W);
            var img = ch.Texture?.PrimaryImage?.Content;
            if (img is { IsEmpty: false } mi)
            {
                try
                {
                    var stream = new MemoryStream(mi.Content.ToArray());
                    phong.DiffuseMap = TextureModel.Create(stream);
                }
                catch { /* unsupported image – keep flat colour */ }
            }
        }
        _materialCache[m] = phong;
        return phong;
    }
}
