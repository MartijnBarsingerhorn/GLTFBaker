using System.Text;
using System.Windows;
using GltfBakeTool.Core.Scene;
using GltfBakeTool.ViewModels;
using GltfBakeTool.Viewer;

namespace GltfBakeTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly SceneViewer _viewer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _viewer = new SceneViewer(Viewport);
        _vm.ModelChanged += () => { _viewer.Load(_vm.Document?.Model); UpdateProps(); };
        _vm.SelectionChanged += () =>
        {
            _viewer.SetHighlights(_vm.SelectedNode?.Node, _vm.CheckedNodes(),
                _vm.ColorByGroup ? _vm.PrimitiveGroupColors : null);
            UpdateProps();
        };

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && System.IO.File.Exists(args[1]))
            Loaded += (_, _) => _vm.OpenFile(args[1]);
    }

    private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _vm.SelectedNode = e.NewValue as NodeItem;

    /// <summary>Clicking the already-selected item deselects it (clears the viewer highlight).</summary>
    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TreeViewItem tvi || tvi.DataContext is not NodeItem item) return;
        if (e.ClickCount != 1) return;
        // ignore clicks on the checkbox or the expander toggle
        for (var d = e.OriginalSource as DependencyObject; d != null && d != tvi; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.CheckBox || d is System.Windows.Controls.Primitives.ToggleButton) return;
        // only the item whose header was clicked (not a child item bubbling up)
        if (!IsWithinHeader(tvi, e.OriginalSource as DependencyObject)) return;

        if (item.IsSelected)
        {
            item.IsSelected = false;
            _vm.SelectedNode = null;
            e.Handled = true;
        }
    }

    private static bool IsWithinHeader(System.Windows.Controls.TreeViewItem tvi, DependencyObject? source)
    {
        // walk up from the click source; if we meet another TreeViewItem before tvi, the click was on a child item
        for (var d = source; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d == tvi) return true;
            if (d is System.Windows.Controls.TreeViewItem) return false;
        }
        return false;
    }

    private void NodeTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (_vm.SelectedNode is { } sel) sel.IsSelected = false;
        _vm.SelectedNode = null;
        e.Handled = true;
    }

    private void ZoomExtents_Click(object sender, RoutedEventArgs e) => _viewer.ZoomExtents();

    private void GroupItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupItem g) _vm.SelectGroupCommand.Execute(g);
    }

    private void GroupList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { }

    private void Preview_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not TexturePreview preview) return;
        e.Handled = true;
        var win = new ImageInspectorWindow(preview) { Owner = this };
        win.Show();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _vm.OpenFile(files[0]);
    }

    private void UpdateProps()
    {
        var item = _vm.SelectedNode;
        if (item == null) { PropsBox.Text = ""; return; }
        var n = item.Node;
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {item.Name}");
        sb.AppendLine($"Index: {n.LogicalIndex}");
        sb.AppendLine($"Kind: {item.Kind}");
        sb.AppendLine($"Children: {n.VisualChildren.Count()}");
        var t = n.LocalTransform;
        if (!t.IsSRT) t = t.GetDecomposed();
        sb.AppendLine($"T: {Fmt(t.Translation)}");
        sb.AppendLine($"R: {t.Rotation.X:0.###}, {t.Rotation.Y:0.###}, {t.Rotation.Z:0.###}, {t.Rotation.W:0.###}");
        sb.AppendLine($"S: {Fmt(t.Scale)}");
        sb.AppendLine($"Identity: {NodeInfo.IsIdentity(n.LocalMatrix)}");
        if (n.Mesh != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Mesh: {n.Mesh.Name} (#{n.Mesh.LogicalIndex})");
            foreach (var p in n.Mesh.Primitives)
            {
                var pos = p.GetVertexAccessor("POSITION");
                sb.AppendLine($"  prim {p.LogicalIndex}: {p.DrawPrimitiveType}, {pos?.Count ?? 0} verts, attrs [{string.Join(",", p.VertexAccessors.Keys)}]");
                if (p.Material != null)
                {
                    sb.AppendLine($"    material: {p.Material.Name} (#{p.Material.LogicalIndex}) alpha={p.Material.Alpha} doubleSided={p.Material.DoubleSided}");
                    foreach (var ch in p.Material.Channels)
                    {
                        var tex = ch.Texture;
                        var img = tex?.PrimaryImage;
                        sb.AppendLine($"    - {ch.Key}: {(tex == null ? "no texture" : $"tex#{tex.LogicalIndex} img#{img?.LogicalIndex} {img?.Content.SourcePath ?? img?.Content.MimeType} uv{ch.TextureCoordinate}")}");
                    }
                }
            }
        }
        if (n.Skin != null) sb.AppendLine($"Skin: {n.Skin.Name} ({n.Skin.JointsCount} joints)");
        PropsBox.Text = sb.ToString();
    }

    private static string Fmt(System.Numerics.Vector3 v) => $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}";
}
