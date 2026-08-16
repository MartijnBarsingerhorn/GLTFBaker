using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GltfBakeTool.ViewModels;

namespace GltfBakeTool;

/// <summary>Full-resolution texture inspector: mouse wheel zooms around the cursor, left-drag pans.</summary>
public partial class ImageInspectorWindow : Window
{
    private readonly BitmapSource _bitmap;
    private readonly BitmapSource _bgra;      // for pixel readback
    private readonly int _w, _h;
    private Matrix _m = Matrix.Identity;      // image space (pixels) -> viewport space (DIPs)
    private bool _dragging;
    private Point _lastMouse;
    private bool _fitPending = true;

    private const double MinScale = 0.01, MaxScale = 256;

    public ImageInspectorWindow(TexturePreview preview)
    {
        InitializeComponent();
        _bitmap = preview.LoadFull();
        _w = _bitmap.PixelWidth;
        _h = _bitmap.PixelHeight;
        _bgra = _bitmap.Format == PixelFormats.Bgra32 ? _bitmap : Freeze(new FormatConvertedBitmap(_bitmap, PixelFormats.Bgra32, null, 0));

        Title = $"{preview.Title} — {_w}×{_h}";
        TitleText.Text = $"{preview.Title}  ({_w}×{_h})";

        Img.Source = _bitmap;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.F) Fit();
            else if (e.Key == Key.D1 || e.Key == Key.NumPad1) ActualPixels();
        };
        Loaded += (_, _) => { Viewport.Focus(); Fit(); };
    }

    private static BitmapSource Freeze(BitmapSource b) { b.Freeze(); return b; }

    // ---- view helpers ---------------------------------------------------------------------

    private double Scale => _m.M11;

    private void Apply()
    {
        if (ImageLayer == null || Img == null) return; // XAML still initialising
        // position/size the layer explicitly (layout scaling; independent of the file's DPI metadata)
        ImageLayer.Width = _w * _m.M11;
        ImageLayer.Height = _h * _m.M22;
        System.Windows.Controls.Canvas.SetLeft(ImageLayer, _m.OffsetX);
        System.Windows.Controls.Canvas.SetTop(ImageLayer, _m.OffsetY);
        bool pixelated = ChkPixelated.IsChecked == true && Scale >= 1.0;
        RenderOptions.SetBitmapScalingMode(Img, pixelated ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        UpdateStatus(null);
    }

    private void Fit()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw <= 0 || vh <= 0 || _w == 0 || _h == 0) { _fitPending = true; return; }
        _fitPending = false;
        double s = Math.Min(vw / _w, vh / _h) * 0.98;
        s = Math.Clamp(s, MinScale, MaxScale);
        _m = new Matrix(s, 0, 0, s, (vw - _w * s) / 2, (vh - _h * s) / 2);
        Apply();
    }

    private void ActualPixels()
    {
        // one image pixel per device pixel
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double s = 1.0 / dpi;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        var center = new Point(vw / 2, vh / 2);
        // keep the image point currently at the centre in place
        var inv = _m; inv.Invert();
        var imgCenter = inv.Transform(center);
        _m = new Matrix(s, 0, 0, s, center.X - imgCenter.X * s, center.Y - imgCenter.Y * s);
        Apply();
    }

    private void ZoomAt(Point viewportPoint, double factor)
    {
        double newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = newScale / Scale;
        if (Math.Abs(factor - 1) < 1e-9) return;
        _m.ScaleAt(factor, factor, viewportPoint.X, viewportPoint.Y);
        Apply();
    }

    private void UpdateStatus(Point? mouse)
    {
        string pixel = "";
        if (mouse is { } p)
        {
            var inv = _m; inv.Invert();
            var ip = inv.Transform(p);
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
            if (x >= 0 && y >= 0 && x < _w && y < _h)
            {
                var px = new byte[4];
                _bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                pixel = $" · pixel ({x}, {y}) = RGBA {px[2]}, {px[1]}, {px[0]}, {px[3]}";
            }
        }
        StatusText.Text = $"zoom {Scale * 100:0.#}%{pixel} · Mouse wheel: zoom · left drag: pan · double-click: fit · F fit · 1 actual pixels · Esc close";
    }

    // ---- input ------------------------------------------------------------------------------

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = Math.Pow(1.25, e.Delta / 120.0);
        ZoomAt(e.GetPosition(Viewport), factor);
        UpdateStatus(e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        if (e.ClickCount == 2) { Fit(); return; }
        _dragging = true;
        _lastMouse = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Viewport);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            _m.Translate(p.X - _lastMouse.X, p.Y - _lastMouse.Y);
            _lastMouse = p;
            Apply();
        }
        UpdateStatus(p);
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitPending) Fit();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void Actual_Click(object sender, RoutedEventArgs e) => ActualPixels();
    private void Pixelated_Changed(object sender, RoutedEventArgs e) => Apply();
    private void Checker_Changed(object sender, RoutedEventArgs e)
    {
        if (Checker == null) return; // XAML still initialising
        Checker.Visibility = ChkChecker.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
