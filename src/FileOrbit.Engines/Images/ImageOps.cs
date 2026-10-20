using System.Runtime.InteropServices;
using SkiaSharp;

namespace FileOrbit.Engines.Images;

/// <summary>How a resize request is interpreted.</summary>
public enum ResizeMode
{
    LongestSide,
    Width,
    Height,
    Exact,
    Percent,
}

/// <summary>How an exact width × height is reached.</summary>
public enum ResizeFit
{
    /// <summary>Keep proportions and fit inside the box (one side may be smaller).</summary>
    Contain,

    /// <summary>Keep proportions, fill the box and crop the overflow (centre).</summary>
    Cover,

    /// <summary>Ignore proportions.</summary>
    Stretch,
}

/// <summary>The plan for a resize: the scaled size, then an optional centred crop.</summary>
public readonly record struct ResizePlan(int ScaledWidth, int ScaledHeight, int CropWidth, int CropHeight)
{
    public bool NeedsCrop => CropWidth != ScaledWidth || CropHeight != ScaledHeight;
}

/// <summary>
/// Pixel operations on <see cref="RasterImage"/> (BGRA, premultiplied). Each returns a new image and
/// keeps the source's DPI and metadata.
/// </summary>
public static class ImageOps
{
    public const int MaxDimension = 30_000;

    /// <summary>Works out output dimensions for a resize request. Never enlarges unless <paramref name="allowUpscale"/>.</summary>
    public static ResizePlan PlanResize(int width, int height, ResizeMode mode, int size, int targetWidth, int targetHeight, int percent, ResizeFit fit, bool allowUpscale)
    {
        double scaleX, scaleY;
        switch (mode)
        {
            case ResizeMode.Width:
                scaleX = scaleY = (double)size / width;
                break;
            case ResizeMode.Height:
                scaleX = scaleY = (double)size / height;
                break;
            case ResizeMode.Percent:
                scaleX = scaleY = percent / 100.0;
                break;
            case ResizeMode.Exact:
                scaleX = (double)targetWidth / width;
                scaleY = (double)targetHeight / height;
                if (fit == ResizeFit.Contain)
                {
                    scaleX = scaleY = Math.Min(scaleX, scaleY);
                }
                else if (fit == ResizeFit.Cover)
                {
                    scaleX = scaleY = Math.Max(scaleX, scaleY);
                }

                break;
            default:
                scaleX = scaleY = (double)size / Math.Max(width, height);
                break;
        }

        if (!allowUpscale)
        {
            scaleX = Math.Min(scaleX, 1);
            scaleY = Math.Min(scaleY, 1);
        }

        var scaledWidth = Clamp((int)Math.Round(width * scaleX));
        var scaledHeight = Clamp((int)Math.Round(height * scaleY));
        if (mode == ResizeMode.Exact && fit == ResizeFit.Cover)
        {
            return new ResizePlan(scaledWidth, scaledHeight, Math.Min(scaledWidth, Clamp(targetWidth)), Math.Min(scaledHeight, Clamp(targetHeight)));
        }

        return new ResizePlan(scaledWidth, scaledHeight, scaledWidth, scaledHeight);

        static int Clamp(int value) => Math.Clamp(value, 1, MaxDimension);
    }

    /// <summary>High-quality resample (mipmapped for big reductions, Mitchell cubic otherwise).</summary>
    public static RasterImage Resize(RasterImage image, int width, int height)
    {
        if (width == image.Width && height == image.Height)
        {
            return image;
        }

        using var source = ToBitmap(image);
        var shrinkingALot = width * 2 < image.Width || height * 2 < image.Height;
        var sampling = shrinkingALot
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), sampling)
                            ?? throw new InvalidOperationException("The image could not be resized.");
        return FromBitmap(resized, image);
    }

    public static RasterImage Crop(RasterImage image, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        width = Math.Clamp(width, 1, image.Width - x);
        height = Math.Clamp(height, 1, image.Height - y);
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(image.Pixels, ((y + row) * image.Width + x) * 4, pixels, row * width * 4, width * 4);
        }

        return Derive(image, pixels, width, height);
    }

    /// <summary>Crops the centre (or an edge) of the image to an aspect ratio such as 16:9.</summary>
    public static RasterImage CropToAspect(RasterImage image, double aspect, string anchor = "center")
    {
        int width = image.Width, height = image.Height;
        if ((double)width / height > aspect)
        {
            width = Math.Max(1, (int)Math.Round(height * aspect));
        }
        else
        {
            height = Math.Max(1, (int)Math.Round(width / aspect));
        }

        var x = anchor switch
        {
            "left" => 0,
            "right" => image.Width - width,
            _ => (image.Width - width) / 2,
        };
        var y = anchor switch
        {
            "top" => 0,
            "bottom" => image.Height - height,
            _ => (image.Height - height) / 2,
        };
        return Crop(image, x, y, width, height);
    }

    public static RasterImage RotateClockwise(RasterImage image) => Rotate(image, clockwise: true);

    public static RasterImage RotateCounterClockwise(RasterImage image) => Rotate(image, clockwise: false);

    public static RasterImage Rotate180(RasterImage image) => FlipVertical(FlipHorizontal(image));

    public static RasterImage FlipHorizontal(RasterImage image)
    {
        var source = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        var pixels = new byte[image.Pixels.Length];
        var target = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Width;
            for (var x = 0; x < image.Width; x++)
            {
                target[row + x] = source[row + image.Width - 1 - x];
            }
        }

        return Derive(image, pixels, image.Width, image.Height);
    }

    public static RasterImage FlipVertical(RasterImage image)
    {
        var pixels = new byte[image.Pixels.Length];
        var stride = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * stride, pixels, (image.Height - 1 - y) * stride, stride);
        }

        return Derive(image, pixels, image.Width, image.Height);
    }

    /// <summary>Black and white, sepia, or a punchier high-contrast black and white.</summary>
    public static RasterImage Monochrome(RasterImage image, string style)
    {
        var pixels = (byte[])image.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Rec. 709 luma; premultiplied values keep their relation to alpha under this linear mix.
            var luma = 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
            switch (style)
            {
                case "sepia":
                    pixels[i] = ToByte(luma * 0.82, pixels[i + 3]);
                    pixels[i + 1] = ToByte(luma * 0.95 + 8 * pixels[i + 3] / 255.0, pixels[i + 3]);
                    pixels[i + 2] = ToByte(luma * 1.07 + 20 * pixels[i + 3] / 255.0, pixels[i + 3]);
                    break;
                case "contrast":
                    var alpha = pixels[i + 3];
                    var normalized = alpha == 0 ? 0 : luma / alpha;
                    var curved = 1 / (1 + Math.Exp(-8 * (normalized - 0.5))); // S-curve
                    var value = ToByte(curved * alpha, alpha);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                    break;
                default:
                    var gray = ToByte(luma, pixels[i + 3]);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
                    break;
            }
        }

        return Derive(image, pixels, image.Width, image.Height);
    }

    /// <summary>
    /// Automatic levels: stretches each channel so the darkest and brightest 0.5% become black and white,
    /// fixing dull or slightly colour-cast photos. <paramref name="strength"/> blends with the original (0..1).
    /// </summary>
    public static RasterImage AutoEnhance(RasterImage image, double strength = 1)
    {
        var histograms = new int[3, 256];
        var counted = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var alpha = image.Pixels[i + 3];
            if (alpha < 128)
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                histograms[c, Unpremultiply(image.Pixels[i + c], alpha)]++;
            }

            counted++;
        }

        if (counted == 0)
        {
            return image;
        }

        var lookup = new byte[3, 256];
        for (var c = 0; c < 3; c++)
        {
            var low = Percentile(histograms, c, counted * 0.005);
            var high = Percentile(histograms, c, counted * 0.995);
            if (high - low < 16)
            {
                low = 0; // Nearly flat channel (e.g. a plain graphic): leave it alone.
                high = 255;
            }

            for (var v = 0; v < 256; v++)
            {
                var stretched = Math.Clamp((v - low) * 255.0 / (high - low), 0, 255);
                lookup[c, v] = (byte)Math.Clamp(Math.Round(v + (stretched - v) * strength), 0, 255);
            }
        }

        var pixels = (byte[])image.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0)
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                var straight = lookup[c, Unpremultiply(pixels[i + c], alpha)];
                pixels[i + c] = (byte)((straight * alpha + 127) / 255);
            }
        }

        return Derive(image, pixels, image.Width, image.Height);
    }

    /// <summary>
    /// Removes uniform borders (or fully transparent edges): everything that matches the top-left
    /// corner colour within <paramref name="tolerance"/> is trimmed. Returns the image unchanged when
    /// nothing would remain.
    /// </summary>
    public static RasterImage Trim(RasterImage image, int tolerance)
    {
        var reference = PixelAt(image, 0, 0);
        bool IsBackground(int x, int y)
        {
            var p = PixelAt(image, x, y);
            if (reference.A == 0)
            {
                return p.A <= tolerance / 4;
            }

            return Math.Abs(p.B - reference.B) <= tolerance && Math.Abs(p.G - reference.G) <= tolerance &&
                   Math.Abs(p.R - reference.R) <= tolerance && Math.Abs(p.A - reference.A) <= tolerance;
        }

        int top = 0, bottom = image.Height - 1, left = 0, right = image.Width - 1;
        while (top <= bottom && Enumerable.Range(0, image.Width).All(x => IsBackground(x, top))) top++;
        while (bottom >= top && Enumerable.Range(0, image.Width).All(x => IsBackground(x, bottom))) bottom--;
        while (left <= right && Enumerable.Range(top, bottom - top + 1).All(y => IsBackground(left, y))) left++;
        while (right >= left && Enumerable.Range(top, bottom - top + 1).All(y => IsBackground(right, y))) right--;

        if (top > bottom || left > right)
        {
            return image;
        }

        return Crop(image, left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>Applies an alpha mask (0..1 per pixel) to the image's existing transparency.</summary>
    public static RasterImage ApplyMask(RasterImage image, float[] mask)
    {
        var pixels = new byte[image.Pixels.Length];
        for (int i = 0, p = 0; i < pixels.Length; i += 4, p++)
        {
            var alpha = image.Pixels[i + 3];
            var newAlpha = (byte)Math.Round(alpha * Math.Clamp(mask[p], 0f, 1f));
            for (var c = 0; c < 3; c++)
            {
                var straight = Unpremultiply(image.Pixels[i + c], alpha);
                pixels[i + c] = (byte)((straight * newAlpha + 127) / 255);
            }

            pixels[i + 3] = newAlpha;
        }

        return Derive(image, pixels, image.Width, image.Height);
    }

    public static (byte B, byte G, byte R, byte A) PixelAt(RasterImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }

    internal static SKBitmap ToBitmap(RasterImage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    private static RasterImage FromBitmap(SKBitmap bitmap, RasterImage template)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return Derive(template, pixels, bitmap.Width, bitmap.Height);
    }

    private static RasterImage Rotate(RasterImage image, bool clockwise)
    {
        var source = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        var pixels = new byte[image.Pixels.Length];
        var target = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        int width = image.Width, height = image.Height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // New image is height × width.
                var (nx, ny) = clockwise ? (height - 1 - y, x) : (y, width - 1 - x);
                target[ny * height + nx] = source[y * width + x];
            }
        }

        return new RasterImage(pixels, height, width, image.DpiY, image.DpiX) { Metadata = image.Metadata };
    }

    private static RasterImage Derive(RasterImage template, byte[] pixels, int width, int height) =>
        new(pixels, width, height, template.DpiX, template.DpiY) { Metadata = template.Metadata, SourceFrameCount = template.SourceFrameCount };

    private static byte ToByte(double value, byte alpha) => (byte)Math.Clamp(Math.Round(value), 0, alpha);

    private static int Unpremultiply(byte value, byte alpha) => alpha == 0 ? 0 : Math.Min(255, (value * 255 + alpha / 2) / alpha);

    private static int Percentile(int[,] histograms, int channel, double rank)
    {
        var total = 0;
        for (var v = 0; v < 256; v++)
        {
            total += histograms[channel, v];
            if (total >= rank)
            {
                return v;
            }
        }

        return 255;
    }
}
