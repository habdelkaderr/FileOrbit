namespace FileOrbit.Engines.Images;

/// <summary>
/// Reads and writes raster images with the platform's codecs: Windows Imaging Component on Windows
/// (<see cref="WindowsImaging"/>), Skia plus the system image tools elsewhere (<see cref="PortableImaging"/>).
/// Image engines go through this class so they work unchanged on every platform.
/// </summary>
public static class ImageCodecs
{
    /// <summary>Refuse images that would need more than ~1.6 GB of pixel memory.</summary>
    public const long MaxPixels = 400_000_000;

#if WINDOWS
    public static IReadOnlySet<string> SupportedDecoderExtensions => WindowsImaging.SupportedDecoderExtensions;

    /// <summary>True when <paramref name="extension"/> (".heic", ".webp", ...) can really be decoded here.</summary>
    public static bool CanDecode(string extension) => WindowsImaging.CanDecode(extension);

    /// <summary>True when images can be written as <paramref name="formatId"/> ("png", "tiff", ...).</summary>
    public static bool CanEncode(string formatId) => WindowsImaging.EncoderIdFor(formatId) is not null;

    public static Task<RasterImage> DecodeAsync(string path, int? maxDimension, bool readMetadata, CancellationToken cancellationToken) =>
        WindowsImaging.DecodeAsync(path, maxDimension, readMetadata, cancellationToken);

    /// <summary>Encodes; returns false when the image's metadata could not be stored.</summary>
    public static Task<bool> EncodeAsync(RasterImage image, string formatId, string outputPath, double quality, MetadataMode metadataMode, CancellationToken cancellationToken) =>
        WindowsImaging.EncodeAsync(image, formatId, outputPath, quality, metadataMode, cancellationToken);

    public static Task<byte[]> EncodePngBytesAsync(RasterImage image, int size, CancellationToken cancellationToken) =>
        WindowsImaging.EncodePngBytesAsync(image, size, cancellationToken);
#else
    public static IReadOnlySet<string> SupportedDecoderExtensions => PortableImaging.SupportedDecoderExtensions;

    /// <summary>True when <paramref name="extension"/> (".heic", ".webp", ...) can really be decoded here.</summary>
    public static bool CanDecode(string extension) => PortableImaging.CanDecode(extension);

    /// <summary>True when images can be written as <paramref name="formatId"/> ("png", "tiff", ...).</summary>
    public static bool CanEncode(string formatId) => PortableImaging.CanEncode(formatId);

    public static Task<RasterImage> DecodeAsync(string path, int? maxDimension, bool readMetadata, CancellationToken cancellationToken) =>
        PortableImaging.DecodeAsync(path, maxDimension, cancellationToken);

    /// <summary>Encodes; returns false when the image's metadata could not be stored.</summary>
    public static Task<bool> EncodeAsync(RasterImage image, string formatId, string outputPath, double quality, MetadataMode metadataMode, CancellationToken cancellationToken) =>
        PortableImaging.EncodeAsync(image, formatId, outputPath, quality, cancellationToken);

    public static Task<byte[]> EncodePngBytesAsync(RasterImage image, int size, CancellationToken cancellationToken) =>
        Task.FromResult(PortableImaging.EncodePngBytes(image, size));
#endif

    public static (uint Width, uint Height) FitWithin(uint width, uint height, int? maxDimension)
    {
        if (maxDimension is not > 0 || (width <= maxDimension && height <= maxDimension))
        {
            return (width, height);
        }

        var scale = (double)maxDimension.Value / Math.Max(width, height);
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }
}

public enum MetadataMode
{
    Keep,
    KeepWithoutLocation,
    Remove,
}
