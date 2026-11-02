using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Processes;
using SkiaSharp;

namespace FileOrbit.Engines.Images;

/// <summary>
/// Image decoding and encoding without Windows Imaging Component: Skia for PNG, JPEG, WebP, GIF, BMP and
/// ICO, plus macOS's built-in <c>sips</c> tool for HEIC, AVIF and TIFF (and for writing BMP, GIF and
/// TIFF, which Skia cannot encode). Used on macOS; compiled everywhere so it can be tested on Windows.
/// Photo metadata (camera, date, location) is not carried over.
/// </summary>
public static class PortableImaging
{
    private const string SipsPath = "/usr/bin/sips";

    private static readonly HashSet<string> SkiaDecodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".gif", ".bmp", ".ico", ".wbmp",
    };

    private static readonly Lazy<HashSet<string>> SipsDecodes = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".tif", ".tiff" };
        if (OperatingSystem.IsMacOSVersionAtLeast(13))
        {
            set.Add(".avif"); // ImageIO reads AVIF from macOS 13 (Ventura).
        }

        return set;
    });

    /// <summary>Whether macOS's image tool is available (it ships with every Mac).</summary>
    public static bool HasSips => OperatingSystem.IsMacOS() && File.Exists(SipsPath);

    public static IReadOnlySet<string> SupportedDecoderExtensions =>
        HasSips ? SkiaDecodes.Union(SipsDecodes.Value, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase) : SkiaDecodes;

    public static bool CanDecode(string extension) => SkiaDecodes.Contains(extension) || (HasSips && SipsDecodes.Value.Contains(extension));

    public static bool CanEncode(string formatId) => formatId is "png" or "jpeg" || (HasSips && formatId is "bmp" or "gif" or "tiff");

    /// <summary>Decodes the first frame, applying EXIF orientation and converting to sRGB.</summary>
    public static async Task<RasterImage> DecodeAsync(string path, int? maxDimension, CancellationToken cancellationToken)
    {
        var image = await Task.Run(() => TryDecodeWithSkia(path, maxDimension), cancellationToken);
        if (image is not null)
        {
            return image;
        }

        if (!HasSips)
        {
            throw new ConversionException("This computer cannot read this image. It may be damaged, or its format is not supported here.");
        }

        // Formats Skia cannot read (HEIC, AVIF, TIFF): let macOS convert to PNG first.
        using var scratch = new ScratchFolder();
        var png = Path.Combine(scratch.Path, "decoded.png");
        await RunSipsAsync(["-s", "format", "png", path, "--out", png], cancellationToken);
        return await Task.Run(() => TryDecodeWithSkia(png, maxDimension), cancellationToken)
               ?? throw new ConversionException("macOS could not read this image. It may be damaged.");
    }

    /// <summary>Encodes; returns false when the image had metadata (it is never stored).</summary>
    public static async Task<bool> EncodeAsync(RasterImage image, string formatId, string outputPath, double quality, CancellationToken cancellationToken)
    {
        switch (formatId)
        {
            case "png":
                await Task.Run(() => WriteSkia(image, outputPath, SKEncodedImageFormat.Png, 100), cancellationToken);
                break;
            case "jpeg":
                await Task.Run(() => WriteSkia(image.FlattenOnto(), outputPath, SKEncodedImageFormat.Jpeg, (int)Math.Round(Math.Clamp(quality, 0.05, 1.0) * 100)), cancellationToken);
                break;
            case "bmp" or "gif" or "tiff" when HasSips:
                using (var scratch = new ScratchFolder())
                {
                    var png = Path.Combine(scratch.Path, "encode.png");
                    var source = formatId == "bmp" ? image.FlattenOnto() : image;
                    await Task.Run(() => WriteSkia(source, png, SKEncodedImageFormat.Png, 100), cancellationToken);
                    if (File.Exists(outputPath))
                    {
                        throw new IOException($"The file '{outputPath}' already exists.");
                    }

                    await RunSipsAsync(["-s", "format", formatId, png, "--out", outputPath], cancellationToken);
                }

                break;
            default:
                throw new ConversionException($"This computer cannot write {formatId.ToUpperInvariant()} images.");
        }

        return image.Metadata.Count == 0;
    }

    /// <summary>Encodes a <paramref name="size"/>×<paramref name="size"/> PNG in memory (for icon files).</summary>
    public static byte[] EncodePngBytes(RasterImage image, int size)
    {
        using var source = ImageOps.ToBitmap(image);
        using var scaled = source.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                           ?? throw new ConversionException("Could not resize the image.");
        using var data = scaled.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ConversionException("The PNG encoder failed.");
        return data.ToArray();
    }

    private static RasterImage? TryDecodeWithSkia(string path, int? maxDimension)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > ImageCodecs.MaxPixels)
        {
            throw new ConversionException($"The image is too large to convert safely ({info.Width}×{info.Height} pixels).");
        }

        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ConversionException("The image could not be decoded. It may be damaged.", result.ToString());
        }

        using var oriented = Orient(decoded, codec.EncodedOrigin);
        var (width, height) = ImageCodecs.FitWithin((uint)oriented.Width, (uint)oriented.Height, maxDimension);
        using var scaled = width == oriented.Width && height == oriented.Height
            ? oriented.Copy()
            : oriented.Resize(new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null)
        {
            throw new ConversionException("Could not resize the image.");
        }

        return new RasterImage(PixelsOf(scaled), scaled.Width, scaled.Height)
        {
            SourceFrameCount = Math.Max(1, codec.FrameCount),
        };
    }

    /// <summary>Applies the EXIF orientation so the pixels are upright.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        // x' = ScaleX·x + SkewX·y + TransX;  y' = SkewY·x + ScaleY·y + TransY
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is null)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix.Value);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    private static byte[] PixelsOf(SKBitmap bitmap)
    {
        var rowBytes = bitmap.Width * 4;
        var span = bitmap.GetPixelSpan();
        if (bitmap.RowBytes == rowBytes)
        {
            return span.ToArray();
        }

        var pixels = new byte[rowBytes * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            span.Slice(y * bitmap.RowBytes, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        }

        return pixels;
    }

    private static void WriteSkia(RasterImage image, string outputPath, SKEncodedImageFormat format, int quality)
    {
        using var bitmap = ImageOps.ToBitmap(image);
        using var data = bitmap.Encode(format, quality) ?? throw new ConversionException($"The {format} encoder failed.");
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        data.SaveTo(output);
    }

    private static async Task RunSipsAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(SipsPath, arguments, cancellationToken, timeout: TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
        {
            throw new ConversionException("macOS could not convert this image.", result.ErrorTail());
        }
    }

    /// <summary>A private temporary folder, deleted on dispose.</summary>
    private sealed class ScratchFolder : IDisposable
    {
        public ScratchFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileOrbit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the OS cleans its temp folder eventually.
            }
        }
    }
}
