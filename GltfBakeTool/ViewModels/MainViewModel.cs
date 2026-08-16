using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GltfBakeTool.Core;
using GltfBakeTool.Core.Grouping;
using GltfBakeTool.Core.Operations;
using GltfBakeTool.Core.Scene;
using Microsoft.Win32;
using SharpGLTF.Schema2;

namespace GltfBakeTool.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private GltfDocument? _document;
    [ObservableProperty] private string _status = "Open a .glb/.gltf file to begin.";
    [ObservableProperty] private string _title = "GLTF Bake Tool";
    [ObservableProperty] private NodeItem? _selectedNode;

    /// <summary>Clean-up option: also remove empties with a non-identity transform by folding it into their children.</summary>
    [ObservableProperty] private bool _foldTransforms;

    // ---- join / atlas options ----
    public int[] AtlasSizes { get; } = { 512, 1024, 2048, 4096, 8192 };
    [ObservableProperty] private int _atlasSize = 4096;
    [ObservableProperty] private int _atlasPadding = 4;
    [ObservableProperty] private int _maxTileRepeats = 4;
    [ObservableProperty] private bool _jpegColor;
    [ObservableProperty] private string _joinName = "Joined";
    public Core.Atlas.AlphaPolicy[] AlphaPolicies { get; } = Enum.GetValues<Core.Atlas.AlphaPolicy>();
    [ObservableProperty] private Core.Atlas.AlphaPolicy _alphaPolicy = Core.Atlas.AlphaPolicy.Auto;
    [ObservableProperty] private double _alphaCutoff = 0.5;

    /// <summary>Texture previews for the selected node's material(s).</summary>
    public ObservableCollection<TexturePreview> TexturePreviews { get; } = new();

    // ---- join groups ----
    public ObservableCollection<GroupItem> Groups { get; } = new();
    [ObservableProperty] private bool _splitBlend = true;
    [ObservableProperty] private bool _splitMaskFromOpaque;
    [ObservableProperty] private bool _splitUnlit = true;
    [ObservableProperty] private bool _splitTransmission = true;
    [ObservableProperty] private bool _splitClearcoat = true;
    [ObservableProperty] private bool _splitOtherExtensions;
    [ObservableProperty] private bool _splitUvSet = true;
    [ObservableProperty] private bool _splitHighTiling;
    [ObservableProperty] private bool _splitDoubleSided;
    /// <summary>Tint meshes in the viewport by join group.</summary>
    [ObservableProperty] private bool _colorByGroup;
    /// <summary>Tint checked nodes (blue) in the viewport.</summary>
    [ObservableProperty] private bool _tintChecked = true;
    partial void OnTintCheckedChanged(bool value) => SelectionChanged?.Invoke();

    public GroupCriteria Criteria => new()
    {
        SplitBlend = SplitBlend,
        SplitMaskFromOpaque = SplitMaskFromOpaque,
        SplitUnlit = SplitUnlit,
        SplitTransmission = SplitTransmission,
        SplitClearcoat = SplitClearcoat,
        SplitOtherExtensions = SplitOtherExtensions,
        SplitUvSet = SplitUvSet,
        SplitHighTiling = SplitHighTiling,
        MaxTileRepeats = Math.Max(1, MaxTileRepeats),
        SplitDoubleSided = SplitDoubleSided,
    };

    /// <summary>Group colour per primitive (for the viewport) – rebuilt with the groups.</summary>
    public Dictionary<MeshPrimitive, System.Windows.Media.Color> PrimitiveGroupColors { get; } = new();

    partial void OnSplitBlendChanged(bool value) => RecomputeGroups();
    partial void OnSplitMaskFromOpaqueChanged(bool value) => RecomputeGroups();
    partial void OnSplitUnlitChanged(bool value) => RecomputeGroups();
    partial void OnSplitTransmissionChanged(bool value) => RecomputeGroups();
    partial void OnSplitClearcoatChanged(bool value) => RecomputeGroups();
    partial void OnSplitOtherExtensionsChanged(bool value) => RecomputeGroups();
    partial void OnSplitUvSetChanged(bool value) => RecomputeGroups();
    partial void OnSplitHighTilingChanged(bool value) => RecomputeGroups();
    partial void OnSplitDoubleSidedChanged(bool value) => RecomputeGroups();
    partial void OnMaxTileRepeatsChanged(int value) { if (SplitHighTiling) RecomputeGroups(); }
    partial void OnColorByGroupChanged(bool value) => SelectionChanged?.Invoke();

    /// <summary>Recomputes the join groups for the whole scene and refreshes tree badges + viewport colours.</summary>
    public void RecomputeGroups()
    {
        Groups.Clear();
        PrimitiveGroupColors.Clear();
        var byNode = new Dictionary<Node, List<GroupItem>>();
        if (Document != null)
        {
            var scene = Document.Model.DefaultScene ?? Document.Model.LogicalScenes.FirstOrDefault();
            var scope = scene?.VisualChildren.SelectMany(GeometryExtractor.Flatten);
            foreach (var g in JoinGrouping.Compute(Document.Model, Criteria, scope))
            {
                var item = new GroupItem { Group = g, Color = GroupItem.ColorFor(g.Index) };
                Groups.Add(item);
                foreach (var (node, prim) in g.Primitives)
                {
                    PrimitiveGroupColors[prim] = item.Color;
                    if (!byNode.TryGetValue(node, out var list)) byNode[node] = list = new();
                    if (!list.Contains(item)) list.Add(item);
                }
            }
        }
        foreach (var it in AllItems())
        {
            it.GroupBrushes.Clear();
            if (byNode.TryGetValue(it.Node, out var list))
            {
                foreach (var g in list) it.GroupBrushes.Add(g.Brush);
                it.GroupToolTip = list.Count == 1
                    ? $"Join group {list[0].Index}: {list[0].Label}"
                    : "Primitives in several join groups: " + string.Join(", ", list.Select(g => $"{g.Index} ({g.Label})"));
            }
            else it.GroupToolTip = null;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Checks exactly the nodes of a group (unchecks everything else).</summary>
    [RelayCommand]
    private void SelectGroup(GroupItem? group)
    {
        if (group == null) return;
        foreach (var it in AllItems()) it.IsChecked = false;
        foreach (var it in AllItems())
            if (group.Group.Nodes.Contains(it.Node)) it.IsChecked = true;
        AddLog($"Checked {group.Group.Nodes.Count} node(s) of group {group.Index} ({group.Label}).");
    }

    public CleanEmptyNodesOptions CleanOptions => new()
    {
        FoldNonIdentityTransforms = FoldTransforms,
        OnlyNodes = CheckedScope(),
    };

    public ObservableCollection<NodeItem> RootNodes { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Raised whenever the model changed and the viewer must rebuild.</summary>
    public event Action? ModelChanged;
    /// <summary>Raised when selection / checks changed (viewer highlights).</summary>
    public event Action? SelectionChanged;

    private readonly Stack<byte[]> _undo = new();

    public bool HasDocument => Document != null;

    [RelayCommand]
    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "glTF files|*.glb;*.gltf|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        OpenFile(dlg.FileName);
    }

    public void OpenFile(string path)
    {
        try
        {
            RootNodes.Clear();
            SelectedNode = null;
            Document = GltfDocument.Load(path);
            _undo.Clear();
            Title = $"GLTF Bake Tool — {Path.GetFileName(path)}";
            RebuildTree();
            AddLog($"Loaded {path}");
            Status = Describe(Document.Model);
        }
        catch (Exception ex)
        {
            AddLog($"Failed to load {path}: {ex.Message}");
            Status = "Load failed.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SaveAs()
    {
        if (Document == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Binary glTF|*.glb|glTF JSON|*.gltf",
            FileName = Path.GetFileNameWithoutExtension(Document.FilePath ?? "model") + "_baked.glb",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Document.Save(dlg.FileName);
            AddLog($"Saved {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AddLog($"Save failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Document == null || _undo.Count == 0) return;
        Document.Restore(_undo.Pop());
        RebuildTree();
        AddLog("Undo.");
        UndoCommand.NotifyCanExecuteChanged();
    }

    public bool CanUndo => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void CleanEmpty()
    {
        var opts = CleanOptions;
        CleanEmptyNodesReport? report = null;
        RunOperation("Clean empty nodes", m => CleanEmptyNodes.Run(m, opts, out report));
        if (report == null) return;
        foreach (var w in report.Warnings) AddLog("  ! " + w);
        foreach (var r in report.Removed) AddLog("  - removed " + r);
        AddLog(report.ToString());
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Join() => RunJoin(perGroup: false);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void JoinPerGroup() => RunJoin(perGroup: true);

    private void RunJoin(bool perGroup)
    {
        var nodes = CheckedNodes().Select(n => n.LogicalIndex).ToList();
        if (nodes.Count == 0)
        {
            AddLog("Join: check the nodes to join first (checkboxes in the tree, or click a join group).");
            return;
        }
        var opts = new JoinOptions
        {
            Name = string.IsNullOrWhiteSpace(JoinName) ? "Joined" : JoinName.Trim(),
            Atlas = new Core.Atlas.AtlasOptions
            {
                MaxAtlasSize = AtlasSize,
                Padding = Math.Max(0, AtlasPadding),
                MaxTileRepeats = Math.Max(1, MaxTileRepeats),
                JpegForColor = JpegColor,
                Alpha = AlphaPolicy,
                AlphaCutoff = (float)Math.Clamp(AlphaCutoff, 0, 1),
            },
            Grouping = perGroup ? Criteria : null,
        };
        JoinReport? report = null;
        RunOperation(perGroup ? "Join per group" : "Join", m => JoinMeshes.Run(m, nodes, opts, out report), keepChecks: false);
        if (report == null) return;
        foreach (var g in report.Groups)
        {
            foreach (var c in g.CellTable) AddLog("    cell " + c);
            foreach (var w in g.Warnings) AddLog("    ! " + w);
            AddLog("  " + g);
        }
        foreach (var w in report.Warnings) AddLog("  ! " + w);
        if (report.PruneSummary != null) AddLog("  " + report.PruneSummary);
        AddLog(report.ToString());
        var first = report.Groups.FirstOrDefault(g => g.NewNodeIndex >= 0);
        if (first != null)
        {
            var item = AllItems().FirstOrDefault(i => i.Node.LogicalIndex == first.NewNodeIndex);
            if (item != null) { ExpandTo(item); item.IsSelected = true; SelectedNode = item; }
        }
    }

    private static void ExpandTo(NodeItem item)
    {
        for (var p = item.Parent; p != null; p = p.Parent) p.IsExpanded = true;
    }

    /// <summary>Checked nodes plus their descendants, or null when nothing is checked (= whole file).</summary>
    private HashSet<int>? CheckedScope()
    {
        var items = AllItems().Where(i => i.IsChecked).ToList();
        if (items.Count == 0) return null;
        var set = new HashSet<int>();
        foreach (var it in items)
        {
            set.Add(it.Node.LogicalIndex);
            foreach (var d in it.Descendants()) set.Add(d.Node.LogicalIndex);
        }
        return set;
    }

    partial void OnFoldTransformsChanged(bool value) => RefreshRemovableFlags();

    private void RefreshRemovableFlags()
    {
        if (Document == null) return;
        RebuildTree(raiseModelChanged: false);
    }

    /// <summary>Run a model-mutating operation with an undo snapshot.</summary>
    /// <param name="keepChecks">Restore the checked nodes afterwards (false: the checks served their purpose, clear them).</param>
    public void RunOperation(string name, Func<ModelRoot, ModelRoot> op, bool keepChecks = true)
    {
        if (Document == null) return;
        try
        {
            _undo.Push(Document.Snapshot());
            var result = op(Document.Model);
            Document.Replace(result);
            if (!keepChecks) foreach (var it in AllItems()) it.IsChecked = false;
            RebuildTree();
            AddLog($"{name}: done. {Describe(result)}");
            Status = Describe(result);
        }
        catch (Exception ex)
        {
            App.LogException(name, ex);
            AddLog($"{name} failed: {ex.Message} (details in {App.LogPath})");
            if (_undo.Count > 0) Document.Restore(_undo.Pop());
            RebuildTree();
        }
        UndoCommand.NotifyCanExecuteChanged();
    }

    public IEnumerable<NodeItem> AllItems()
    {
        foreach (var r in RootNodes)
        {
            yield return r;
            foreach (var d in r.Descendants()) yield return d;
        }
    }

    public IReadOnlyList<Node> CheckedNodes() => AllItems().Where(i => i.IsChecked).Select(i => i.Node).ToList();

    private void RebuildTree(bool raiseModelChanged = true)
    {
        // preserve check + selection state across rebuilds (node indices change, names/paths mostly don't)
        var checkedPaths = AllItems().Where(i => i.IsChecked && (i.Parent == null || !i.Parent.IsChecked)).Select(i => NodePath.Of(i.Node)).ToHashSet();
        var selectedPath = SelectedNode != null ? NodePath.Of(SelectedNode.Node) : null;

        RootNodes.Clear();
        SelectedNode = null;
        if (Document != null)
        {
            var scene = Document.Model.DefaultScene ?? Document.Model.LogicalScenes.FirstOrDefault();
            var prot = NodeInfo.CollectProtectedNodes(Document.Model);
            var removable = CleanEmptyNodes.FindRemovable(Document.Model, new CleanEmptyNodesOptions { FoldNonIdentityTransforms = FoldTransforms }).ToHashSet();
            if (scene != null)
                foreach (var n in scene.VisualChildren)
                {
                    var item = new NodeItem(n, null, prot, removable);
                    Hook(item);
                    RootNodes.Add(item);
                }
        }
        foreach (var it in AllItems())
        {
            var path = NodePath.Of(it.Node);
            if (checkedPaths.Contains(path)) it.IsChecked = true;
            if (selectedPath != null && path == selectedPath) { ExpandTo(it); it.IsSelected = true; SelectedNode = it; }
        }

        OnPropertyChanged(nameof(HasDocument));
        SaveAsCommand.NotifyCanExecuteChanged();
        CleanEmptyCommand.NotifyCanExecuteChanged();
        JoinCommand.NotifyCanExecuteChanged();
        JoinPerGroupCommand.NotifyCanExecuteChanged();
        if (raiseModelChanged) ModelChanged?.Invoke();
        RecomputeGroups();
    }

    private void Hook(NodeItem item)
    {
        item.CheckedChanged += _ => SelectionChanged?.Invoke();
        foreach (var c in item.Children) Hook(c);
    }

    partial void OnSelectedNodeChanged(NodeItem? value)
    {
        RefreshTexturePreviews();
        SelectionChanged?.Invoke();
    }

    private void RefreshTexturePreviews()
    {
        TexturePreviews.Clear();
        var mesh = SelectedNode?.Node.Mesh;
        if (mesh == null) return;
        var seen = new HashSet<(int, string)>();
        foreach (var prim in mesh.Primitives)
        {
            var mat = prim.Material;
            if (mat == null) continue;
            foreach (var ch in mat.Channels)
            {
                var img = ch.Texture?.PrimaryImage;
                if (img == null || !seen.Add((mat.LogicalIndex, ch.Key))) continue;
                var preview = TexturePreview.TryCreate(mat, ch.Key, img);
                if (preview != null) TexturePreviews.Add(preview);
            }
        }
    }

    public void AddLog(string line) => Log.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {line}");

    private static string Describe(ModelRoot m)
    {
        int prims = m.LogicalMeshes.Sum(x => x.Primitives.Count);
        return $"{m.LogicalNodes.Count} nodes · {m.LogicalMeshes.Count} meshes · {prims} primitives · {m.LogicalMaterials.Count} materials · {m.LogicalTextures.Count} textures · {m.LogicalAnimations.Count} animations · {m.LogicalSkins.Count} skins";
    }
}
