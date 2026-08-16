using System.IO;
using System.Windows.Media.Imaging;
using SharpGLTF.Schema2;

namespace GltfBakeTool.ViewModels;

/// <summary>A decoded (downscaled) texture for the properties panel.</summary>
public sealed class TexturePreview
{
    public required string Title { get; init; }
    public required BitmapSource Image { get; init; }
    public required string Info { get; init; }
    public required byte[] Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Decodes the image at full resolution (for the inspector window).</summary>
    public BitmapSource LoadFull()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(Bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static TexturePreview? TryCreate(Material mat, string channel, Image img)
    {
        try
        {
            var bytes = img.Content.Content.ToArray();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();

            int w = 0, h = 0;
            try
            {
                var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                w = frame.PixelWidth; h = frame.PixelHeight;
            }
            catch { }

            var matName = string.IsNullOrEmpty(mat.Name) ? $"material #{mat.LogicalIndex}" : mat.Name;
            return new TexturePreview
            {
                Title = $"{matName} · {channel}",
                Image = bmp,
                Info = $"image #{img.LogicalIndex} · {img.Content.MimeType} · {w}×{h} · {bytes.Length / 1024:N0} KB · double-click to inspect",
                Bytes = bytes,
                Width = w,
                Height = h,
            };
        }
        catch
        {
            return null;
        }
    }
}
