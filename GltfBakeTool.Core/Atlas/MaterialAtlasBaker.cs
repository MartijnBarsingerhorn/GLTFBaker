using System.Numerics;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace GltfBakeTool.Core.Atlas;

public enum AtlasChannel { BaseColor, MetallicRoughness, Normal, Occlusion, Emissive }

/// <summary>How the merged material's alpha mode is chosen.</summary>
public enum AlphaPolicy
{
    /// <summary>Most permissive mode among the sources (BLEND if any blends, else MASK if any masks, else OPAQUE).</summary>
    Auto,
    Opaque,
    Mask,
    Blend,
}

public sealed record AtlasOptions
{
    /// <summary>Largest atlas edge (power of two).</summary>
    public int MaxAtlasSize { get; init; } = 4096;
    /// <summary>Pixels of edge-extended padding around each cell (prevents mip bleeding).</summary>
    public int Padding { get; init; } = 4;
    /// <summary>UV tiling up to this many repeats per axis is baked into the cell; beyond it UVs are clamped (with a warning).</summary>
    public int MaxTileRepeats { get; init; } = 4;
    /// <summary>Cell size for materials without any texture.</summary>
    public int SolidCellSize { get; init; } = 8;
    public bool IncludeMetallicRoughness { get; init; } = true;
    public bool IncludeNormal { get; init; } = true;
    public bool IncludeOcclusion { get; init; } = true;
    public bool IncludeEmissive { get; init; } = true;
    /// <summary>Encode base colour / emissive atlases as JPEG (only when no alpha is needed).</summary>
    public bool JpegForColor { get; init; } = false;
    public int JpegQuality { get; init; } = 90;
    /// <summary>Alpha mode of the merged material.</summary>
    public AlphaPolicy Alpha { get; init; } = AlphaPolicy.Auto;
    /// <summary>Alpha cutoff used when the merged material is MASK (Auto takes the first masked source's cutoff).</summary>
    public float AlphaCutoff { get; init; } = 0.5f;
}

/// <summary>One source material together with the UV range (after texture transform) that references it.</summary>
public sealed class MaterialUsage
{
    public Material? Material { get; init; }
    public Vector2 UvMin { get; set; } = new(float.MaxValue);
    public Vector2 UvMax { get; set; } = new(float.MinValue);
    public bool HasUvs => UvMin.X <= UvMax.X;
    public void Include(Vector2 uv)
    {
        UvMin = Vector2.Min(UvMin, uv);
        UvMax = Vector2.Max(UvMax, uv);
    }
}

public sealed class AtlasCell
{
    public required int UsageIndex { get; init; }
    public PackRect Content { get; set; }
    public int RepeatsU { get; init; } = 1;
    public int RepeatsV { get; init; } = 1;
    public float OffsetU { get; init; }
    public float OffsetV { get; init; }
    public bool Clamped { get; init; }
    public bool Solid { get; init; }

    /// <summary>Maps a source UV (texture-transform already applied) into atlas UV space.</summary>
    public Vector2 MapUv(Vector2 uv, int atlasW, int atlasH)
    {
        if (Solid)
            return new Vector2((Content.X + Content.W * 0.5f) / atlasW, (Content.Y + Content.H * 0.5f) / atlasH);
        float lu = (uv.X - OffsetU) / RepeatsU;
        float lv = (uv.Y - OffsetV) / RepeatsV;
        lu = Math.Clamp(lu, 0f, 1f);
        lv = Math.Clamp(lv, 0f, 1f);
        return new Vector2((Content.X + lu * Content.W) / atlasW, (Content.Y + lv * Content.H) / atlasH);
    }
}

public sealed class AtlasResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required AtlasCell[] Cells { get; init; }                     // by usage index
    public Dictionary<AtlasChannel, byte[]> Images { get; } = new();     // encoded
    public Dictionary<AtlasChannel, string> MimeTypes { get; } = new();
    /// <summary>Channels whose value is identical for all materials and therefore folded into a factor instead of an image.</summary>
    public float? UniformMetallic { get; set; }
    public float? UniformRoughness { get; set; }
    public Vector3? UniformEmissive { get; set; }
    public AlphaMode Alpha { get; set; } = AlphaMode.OPAQUE;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Bakes the textures/factors of several materials into per-channel atlases sharing one cell layout.</summary>
public static class MaterialAtlasBaker
{
    private sealed class ChannelSource
    {
        public SKBitmap? Bitmap;
        public SKColor Fill;
        public SKColorFilter? Filter;
        public SKShaderTileMode WrapS = SKShaderTileMode.Repeat, WrapT = SKShaderTileMode.Repeat;
    }

    private sealed class Prepared
    {
        public required MaterialUsage Usage;
        public Dictionary<AtlasChannel, ChannelSource> Sources = new();
        public int TexW, TexH;          // native size of the largest texture across channels
        public int RepU = 1, RepV = 1;
        public float OffU, OffV;
        public bool Clamped, Solid;
        public bool Has(AtlasChannel c) => Sources.TryGetValue(c, out var s) && s.Bitmap != null;
    }

    public static AtlasResult Bake(IReadOnlyList<MaterialUsage> usages, AtlasOptions opt)
    {
        var warnings = new List<string>();
        var imageCache = new Dictionary<int, SKBitmap?>();
        var prepared = usages.Select(u => Prepare(u, opt, imageCache, warnings)).ToList();

        // ---- which channels get an atlas ------------------------------------------------------
        var channels = new List<AtlasChannel> { AtlasChannel.BaseColor };
        float? uniformMetal = null, uniformRough = null;
        Vector3? uniformEmissive = null;

        if (opt.IncludeMetallicRoughness)
        {
            bool anyTex = prepared.Any(p => p.Has(AtlasChannel.MetallicRoughness));
            var metals = prepared.Select(p => Metallic(p.Usage.Material)).Distinct().ToList();
            var roughs = prepared.Select(p => Roughness(p.Usage.Material)).Distinct().ToList();
            if (anyTex || metals.Count > 1 || roughs.Count > 1) channels.Add(AtlasChannel.MetallicRoughness);
            else { uniformMetal = metals[0]; uniformRough = roughs[0]; }
        }
        if (opt.IncludeNormal && prepared.Any(p => p.Has(AtlasChannel.Normal))) channels.Add(AtlasChannel.Normal);
        if (opt.IncludeOcclusion && prepared.Any(p => p.Has(AtlasChannel.Occlusion))) channels.Add(AtlasChannel.Occlusion);
        if (opt.IncludeEmissive)
        {
            bool anyTex = prepared.Any(p => p.Has(AtlasChannel.Emissive));
            var factors = prepared.Select(p => EmissiveFactor(p.Usage.Material)).Distinct().ToList();
            if (anyTex || factors.Count > 1) channels.Add(AtlasChannel.Emissive);
            else uniformEmissive = factors[0];
        }

        // ---- layout ---------------------------------------------------------------------------
        int pad = opt.Padding;
        var (W, H, rects, scale) = Layout(prepared, opt, warnings);
        var cells = new AtlasCell[prepared.Count];
        for (int i = 0; i < prepared.Count; i++)
        {
            var p = prepared[i];
            var r = rects[i];
            cells[i] = new AtlasCell
            {
                UsageIndex = i,
                Content = new PackRect(r.X + pad, r.Y + pad, r.W - 2 * pad, r.H - 2 * pad),
                RepeatsU = p.RepU, RepeatsV = p.RepV, OffsetU = p.OffU, OffsetV = p.OffV,
                Clamped = p.Clamped, Solid = p.Solid,
            };
        }

        var result = new AtlasResult { Width = W, Height = H, Cells = cells };
        result.Warnings.AddRange(warnings);
        result.UniformMetallic = uniformMetal;
        result.UniformRoughness = uniformRough;
        result.UniformEmissive = uniformEmissive;

        // ---- material-level flags -------------------------------------------------------------
        foreach (var u in usages)
        {
            var m = u.Material;
            if (m == null) continue;
            if (m.DoubleSided) result.DoubleSided = true;
            if (m.Alpha == AlphaMode.BLEND) result.Alpha = AlphaMode.BLEND;
            else if (m.Alpha == AlphaMode.MASK && result.Alpha == AlphaMode.OPAQUE) { result.Alpha = AlphaMode.MASK; result.AlphaCutoff = m.AlphaCutoff; }
        }
        var sourceModes = usages.Select(u => u.Material?.Alpha ?? AlphaMode.OPAQUE).Distinct().ToList();
        switch (opt.Alpha)
        {
            case AlphaPolicy.Opaque: result.Alpha = AlphaMode.OPAQUE; break;
            case AlphaPolicy.Mask: result.Alpha = AlphaMode.MASK; result.AlphaCutoff = opt.AlphaCutoff; break;
            case AlphaPolicy.Blend: result.Alpha = AlphaMode.BLEND; break;
        }
        if (opt.Alpha == AlphaPolicy.Auto && sourceModes.Count > 1)
            result.Warnings.Add($"Source materials use different alpha modes ({string.Join("/", sourceModes)}); merged material uses {result.Alpha}.");
        else if (opt.Alpha != AlphaPolicy.Auto && sourceModes.Any(m => m != result.Alpha))
            result.Warnings.Add($"Merged material forced to {result.Alpha}; sources used {string.Join("/", sourceModes)}"
                + (result.Alpha == AlphaMode.OPAQUE && sourceModes.Contains(AlphaMode.BLEND) ? " – transparent parts will render solid." : "."));

        // ---- compose ---------------------------------------------------------------------------
        foreach (var ch in channels)
        {
            using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(DefaultColor(ch));

            for (int i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                var cell = cells[i];
                var padded = SKRect.Create(cell.Content.X - pad, cell.Content.Y - pad, cell.Content.W + 2 * pad, cell.Content.H + 2 * pad);
                padded.Intersect(SKRect.Create(0, 0, W, H));

                p.Sources.TryGetValue(ch, out var src);
                src ??= new ChannelSource { Fill = DefaultColor(ch) };

                if (src.Bitmap != null)
                {
                    float sx = (float)cell.Content.W / (p.RepU * src.Bitmap.Width);
                    float sy = (float)cell.Content.H / (p.RepV * src.Bitmap.Height);
                    var matrix = SKMatrix.CreateScaleTranslation(sx, sy, cell.Content.X, cell.Content.Y);
                    using var shader = src.Bitmap.ToShader(src.WrapS, src.WrapT, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), matrix);
                    using var paint = new SKPaint { Shader = shader, ColorFilter = src.Filter, IsAntialias = false, BlendMode = SKBlendMode.Src };
                    canvas.DrawRect(padded, paint);
                }
                else
                {
                    using var paint = new SKPaint { Color = src.Fill, IsAntialias = false, BlendMode = SKBlendMode.Src };
                    canvas.DrawRect(padded, paint);
                }
            }
            canvas.Flush();

            bool color = ch is AtlasChannel.BaseColor or AtlasChannel.Emissive;
            bool jpeg = opt.JpegForColor && color && !(ch == AtlasChannel.BaseColor && result.Alpha != AlphaMode.OPAQUE);
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? opt.JpegQuality : 100);
            result.Images[ch] = data.ToArray();
            result.MimeTypes[ch] = jpeg ? "image/jpeg" : "image/png";
        }

        foreach (var p in prepared) foreach (var s in p.Sources.Values) { s.Filter?.Dispose(); }
        foreach (var b in imageCache.Values) b?.Dispose();
        return result;
    }

    // -------------------------------------------------------------------------------------------

    private static (int W, int H, PackRect[] rects, float scale) Layout(List<Prepared> prepared, AtlasOptions opt, List<string> warnings)
    {
        int pad = opt.Padding;

        List<(int, int)> SizesAt(float scale) => prepared.Select(p =>
        {
            int w = p.Solid ? opt.SolidCellSize : Math.Max(1, (int)MathF.Round(p.TexW * p.RepU * scale));
            int h = p.Solid ? opt.SolidCellSize : Math.Max(1, (int)MathF.Round(p.TexH * p.RepV * scale));
            return (w + 2 * pad, h + 2 * pad);
        }).ToList();

        // For each candidate atlas size (smallest first) accept a modest downscale (down to 75%)
        // before moving on to the next size: a 0.5% shrink beats doubling the atlas.
        float[] nearScales = { 1f, 0.99f, 0.97f, 0.95f, 0.92f, 0.88f, 0.84f, 0.8f, 0.75f };
        long fullArea = SizesAt(1f).Sum(s => (long)s.Item1 * s.Item2);
        foreach (var (W, H) in CandidateSizes(opt.MaxAtlasSize))
        {
            if ((long)W * H < fullArea * 0.75f * 0.75f) continue;
            foreach (var scale in nearScales)
            {
                var sizes = SizesAt(scale);
                if ((long)W * H < sizes.Sum(s => (long)s.Item1 * s.Item2)) continue;
                var rects = SkylinePacker.PackAll(sizes, W, H);
                if (rects != null)
                {
                    if (scale < 0.9f) warnings.Add($"Textures were downscaled to {scale * 100:0}% to fit a {W}×{H} atlas.");
                    return (W, H, rects, scale);
                }
            }
        }

        // Nothing fits the largest atlas: keep shrinking.
        float s2 = 0.75f;
        for (int attempt = 0; attempt < 40; attempt++, s2 *= 0.85f)
        {
            var sizes = SizesAt(s2);
            int W = opt.MaxAtlasSize, H = opt.MaxAtlasSize;
            var rects = SkylinePacker.PackAll(sizes, W, H);
            if (rects != null)
            {
                warnings.Add($"Textures were downscaled to {s2 * 100:0}% to fit a {W}×{H} atlas.");
                return (W, H, rects, s2);
            }
        }
        throw new InvalidOperationException("Could not fit the materials into the atlas even after downscaling.");
    }

    /// <summary>Power-of-two atlas sizes in increasing area: 64², 128×64, 128², 256×128, ...</summary>
    private static IEnumerable<(int W, int H)> CandidateSizes(int max)
    {
        for (int s = 64; s <= max; s *= 2)
        {
            yield return (s, s);
            if (s * 2 <= max) yield return (s * 2, s);
        }
    }

    private static Prepared Prepare(MaterialUsage u, AtlasOptions opt, Dictionary<int, SKBitmap?> cache, List<string> warnings)
    {
        var p = new Prepared { Usage = u };
        var m = u.Material;
        string mname = m == null ? "<default material>" : (string.IsNullOrEmpty(m.Name) ? $"material #{m.LogicalIndex}" : m.Name);

        // channels
        AddChannel(p, m, AtlasChannel.BaseColor, cache, warnings, mname);
        if (opt.IncludeMetallicRoughness) AddChannel(p, m, AtlasChannel.MetallicRoughness, cache, warnings, mname);
        if (opt.IncludeNormal) AddChannel(p, m, AtlasChannel.Normal, cache, warnings, mname);
        if (opt.IncludeOcclusion) AddChannel(p, m, AtlasChannel.Occlusion, cache, warnings, mname);
        if (opt.IncludeEmissive) AddChannel(p, m, AtlasChannel.Emissive, cache, warnings, mname);

        var bitmaps = p.Sources.Values.Where(s => s.Bitmap != null).Select(s => s.Bitmap!).ToList();
        if (bitmaps.Count == 0)
        {
            p.Solid = true;
            return p;
        }
        p.TexW = bitmaps.Max(b => b.Width);
        p.TexH = bitmaps.Max(b => b.Height);

        // UV tiling
        if (u.HasUvs)
        {
            const float eps = 1e-3f;
            int u0 = (int)MathF.Floor(u.UvMin.X + eps), u1 = (int)MathF.Ceiling(u.UvMax.X - eps);
            int v0 = (int)MathF.Floor(u.UvMin.Y + eps), v1 = (int)MathF.Ceiling(u.UvMax.Y - eps);
            int ru = Math.Max(1, u1 - u0), rv = Math.Max(1, v1 - v0);
            if (ru <= opt.MaxTileRepeats && rv <= opt.MaxTileRepeats)
            {
                p.RepU = ru; p.RepV = rv; p.OffU = u0; p.OffV = v0;
            }
            else
            {
                p.RepU = 1; p.RepV = 1; p.OffU = u0; p.OffV = v0; p.Clamped = true;
                warnings.Add($"'{mname}': UVs tile {ru}×{rv} times (limit {opt.MaxTileRepeats}); UVs were clamped to a single tile – texture will stretch.");
            }
        }
        return p;
    }

    private static void AddChannel(Prepared p, Material? m, AtlasChannel ch, Dictionary<int, SKBitmap?> cache, List<string> warnings, string mname)
    {
        var src = new ChannelSource { Fill = DefaultColor(ch) };
        p.Sources[ch] = src;
        if (m == null) return;

        MaterialChannel? channel = ch switch
        {
            AtlasChannel.BaseColor => m.FindChannel("BaseColor") ?? m.FindChannel("Diffuse"),
            AtlasChannel.MetallicRoughness => m.FindChannel("MetallicRoughness"),
            AtlasChannel.Normal => m.FindChannel("Normal"),
            AtlasChannel.Occlusion => m.FindChannel("Occlusion"),
            AtlasChannel.Emissive => m.FindChannel("Emissive"),
            _ => null,
        };

        // ---- factors / fills ----
        switch (ch)
        {
            case AtlasChannel.BaseColor:
            {
                var c = channel?.Color ?? Vector4.One;
                src.Fill = ToSrgb(c);
                if (c != Vector4.One) src.Filter = SKColorFilter.CreateBlendMode(ToSrgb(c), SKBlendMode.Modulate);
                break;
            }
            case AtlasChannel.MetallicRoughness:
            {
                float metal = Metallic(m), rough = Roughness(m);
                src.Fill = new SKColor(255, (byte)Math.Clamp(rough * 255f, 0, 255), (byte)Math.Clamp(metal * 255f, 0, 255));
                break;
            }
            case AtlasChannel.Normal:
                src.Fill = new SKColor(128, 128, 255);
                if (channel is { } nc && TryFactor(nc, "NormalScale") is { } ns && MathF.Abs(ns - 1f) > 1e-3f)
                    warnings.Add($"'{mname}': normal scale {ns:0.##} is not representable in the atlas and was ignored.");
                break;
            case AtlasChannel.Occlusion:
                src.Fill = SKColors.White;
                break;
            case AtlasChannel.Emissive:
            {
                var e = EmissiveFactor(m);
                src.Fill = ToSrgb(new Vector4(e, 1));
                if (e != Vector3.One) src.Filter = SKColorFilter.CreateBlendMode(ToSrgb(new Vector4(e, 1)), SKBlendMode.Modulate);
                if (channel is { } ec && TryFactor(ec, "EmissiveStrength") is { } es && MathF.Abs(es - 1f) > 1e-3f)
                    warnings.Add($"'{mname}': emissive strength {es:0.##} was ignored.");
                break;
            }
        }

        // ---- texture ----
        if (channel?.Texture is not { } tex) return;
        if (channel.Value.TextureCoordinate != 0)
        {
            warnings.Add($"'{mname}': {ch} uses TEXCOORD_{channel.Value.TextureCoordinate}; only TEXCOORD_0 can be atlased – texture dropped for this channel.");
            return;
        }
        var img = tex.PrimaryImage;
        if (img == null) return;
        if (!cache.TryGetValue(img.LogicalIndex, out var bmp))
        {
            bmp = Decode(img);
            if (bmp == null) warnings.Add($"'{mname}': image #{img.LogicalIndex} ({img.Content.MimeType}) could not be decoded – channel {ch} falls back to its factor.");
            cache[img.LogicalIndex] = bmp;
        }
        if (bmp == null) return;

        // per-channel factor baking that cannot be expressed as a colour modulate
        if (ch == AtlasChannel.MetallicRoughness)
        {
            float metal = Metallic(m), rough = Roughness(m);
            if (MathF.Abs(metal - 1) > 1e-3f || MathF.Abs(rough - 1) > 1e-3f)
                bmp = ScaleChannels(bmp, 1f, rough, metal, 1f);
        }
        else if (ch == AtlasChannel.Occlusion)
        {
            var s = channel is { } oc ? TryFactor(oc, "OcclusionStrength") ?? 1f : 1f;
            if (MathF.Abs(s - 1) > 1e-3f) bmp = LerpToWhite(bmp, s);
        }

        src.Bitmap = bmp;
        var sampler = tex.Sampler;
        src.WrapS = Wrap(sampler?.WrapS ?? TextureWrapMode.REPEAT);
        src.WrapT = Wrap(sampler?.WrapT ?? TextureWrapMode.REPEAT);
    }

    private static SKShaderTileMode Wrap(TextureWrapMode w) => w switch
    {
        TextureWrapMode.CLAMP_TO_EDGE => SKShaderTileMode.Clamp,
        TextureWrapMode.MIRRORED_REPEAT => SKShaderTileMode.Mirror,
        _ => SKShaderTileMode.Repeat,
    };

    private static SKBitmap? Decode(Image img)
    {
        try
        {
            var bytes = img.Content.Content.ToArray();
            var decoded = SKBitmap.Decode(bytes);
            if (decoded == null) return null;
            if (decoded.ColorType == SKColorType.Rgba8888 && decoded.AlphaType == SKAlphaType.Unpremul) return decoded;
            var conv = decoded.Copy(SKColorType.Rgba8888);
            decoded.Dispose();
            return conv;
        }
        catch { return null; }
    }

    private static unsafe SKBitmap ScaleChannels(SKBitmap src, float r, float g, float b, float a)
    {
        var dst = src.Copy(SKColorType.Rgba8888);
        var ptr = (byte*)dst.GetPixels();
        long n = (long)dst.Width * dst.Height;
        for (long i = 0; i < n; i++)
        {
            ptr[i * 4 + 0] = (byte)Math.Clamp(ptr[i * 4 + 0] * r, 0, 255);
            ptr[i * 4 + 1] = (byte)Math.Clamp(ptr[i * 4 + 1] * g, 0, 255);
            ptr[i * 4 + 2] = (byte)Math.Clamp(ptr[i * 4 + 2] * b, 0, 255);
            ptr[i * 4 + 3] = (byte)Math.Clamp(ptr[i * 4 + 3] * a, 0, 255);
        }
        return dst;
    }

    private static unsafe SKBitmap LerpToWhite(SKBitmap src, float strength)
    {
        var dst = src.Copy(SKColorType.Rgba8888);
        var ptr = (byte*)dst.GetPixels();
        long n = (long)dst.Width * dst.Height * 4;
        for (long i = 0; i < n; i++)
            if (i % 4 != 3) ptr[i] = (byte)Math.Clamp(255 + strength * (ptr[i] - 255), 0, 255);
        return dst;
    }

    private static SKColor DefaultColor(AtlasChannel ch) => ch switch
    {
        AtlasChannel.BaseColor => SKColors.White,
        AtlasChannel.MetallicRoughness => new SKColor(255, 255, 0),
        AtlasChannel.Normal => new SKColor(128, 128, 255),
        AtlasChannel.Occlusion => SKColors.White,
        AtlasChannel.Emissive => SKColors.Black,
        _ => SKColors.Magenta,
    };

    public static float Metallic(Material? m)
    {
        if (m == null) return 1f;
        if (m.FindChannel("MetallicRoughness") is { } ch) return TryFactor(ch, "MetallicFactor") ?? 1f;
        return 0f; // spec-gloss materials: treat as dielectric
    }

    public static float Roughness(Material? m)
    {
        if (m == null) return 1f;
        if (m.FindChannel("MetallicRoughness") is { } ch) return TryFactor(ch, "RoughnessFactor") ?? 1f;
        if (m.FindChannel("SpecularGlossiness") is { } sg) return 1f - (TryFactor(sg, "GlossinessFactor") ?? 1f);
        return 1f;
    }

    public static Vector3 EmissiveFactor(Material? m)
    {
        if (m?.FindChannel("Emissive") is { } ch) { var c = ch.Color; return new Vector3(c.X, c.Y, c.Z); }
        return Vector3.Zero;
    }

    private static float? TryFactor(MaterialChannel ch, string key)
    {
        try { return ch.GetFactor(key); } catch { return null; }
    }

    private static SKColor ToSrgb(Vector4 linear)
    {
        static byte C(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);
        }
        return new SKColor(C(linear.X), C(linear.Y), C(linear.Z), (byte)Math.Clamp(MathF.Round(Math.Clamp(linear.W, 0, 1) * 255f), 0, 255));
    }
}
