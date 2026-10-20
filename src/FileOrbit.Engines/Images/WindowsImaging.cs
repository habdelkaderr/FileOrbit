using System.Runtime.InteropServices.WindowsRuntime;
using FileOrbit.Core.Capabilities;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FileOrbit.Engines.Images;

/// <summary>
/// Decoding and encoding through Windows Imaging Component (via Windows.Graphics.Imaging). This
/// automatically benefits from codec extensions installed from the Microsoft Store (HEIF, WebP, AVIF, RAW).
/// </summary>
public static class WindowsImaging
{
    /// <summary>Refuse images that would need more than ~1.6 GB of pixel memory.</summary>
    public const long MaxPixels = 400_000_000;

    private static readonly string[] MetadataProperties =
    [
        "System.Photo.DateTaken",
        "System.Photo.CameraManufacturer",
        "System.Photo.CameraModel",
        "System.Photo.FNumber",
        "System.Photo.ExposureTime",
        "System.Photo.ISOSpeed",
        "System.Photo.FocalLength",
        "System.Photo.LensModel",
        "System.Author",
        "System.Copyright",
        "System.Title",
        "System.Subject",
        "System.Keywords",
        "System.Rating",
        "System.ApplicationName",
    ];

    private static readonly string[] LocationProperties =
    [
        "System.GPS.Latitude",
        "System.GPS.LatitudeRef",
        "System.GPS.Longitude",
        "System.GPS.LongitudeRef",
        "System.GPS.Altitude",
        "System.GPS.AltitudeRef",
    ];

    private static readonly Lazy<HashSet<string>> DecoderExtensions = new(() => LoadExtensions(BitmapDecoder.GetDecoderInformationEnumerator()));

    /// <summary>File extensions (".heic", ".webp", ...) that installed WIC decoders can read.</summary>
    public static IReadOnlySet<string> SupportedDecoderExtensions => DecoderExtensions.Value;

    public static bool CanDecode(string extension) => DecoderExtensions.Value.Contains(extension.ToLowerInvariant());

    /// <summary>Decodes the first frame, applying EXIF orientation and converting to sRGB.</summary>
    public static async Task<RasterImage> DecodeAsync(string path, int? maxDimension, bool readMetadata, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var stream = file.AsRandomAccessStream();
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConversionException("Windows could not read this image. It may be damaged, or the codec for its format is not installed.", ex.Message, ex);
        }

        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0 || (long)width * height > MaxPixels)
        {
            throw new ConversionException($"The image is too large to convert safely ({width}×{height} pixels).");
        }

        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        var (scaledWidth, scaledHeight) = FitWithin(width, height, maxDimension);
        if (scaledWidth != width || scaledHeight != height)
        {
            // BitmapTransform scales in the decoder's raw orientation; swap if the image is rotated 90°.
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            transform.ScaledWidth = rotated ? scaledHeight : scaledWidth;
            transform.ScaledHeight = rotated ? scaledWidth : scaledHeight;
        }

        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        var pixels = provider.DetachPixelData();

        var metadata = readMetadata ? await ReadMetadataAsync(decoder) : new Dictionary<string, object>();
        return new RasterImage(pixels, (int)scaledWidth, (int)scaledHeight, decoder.DpiX, decoder.DpiY)
        {
            SourceFrameCount = (int)decoder.FrameCount,
            Metadata = metadata,
        };
    }

    public static Guid? EncoderIdFor(string formatId) => formatId switch
    {
        "png" => BitmapEncoder.PngEncoderId,
        "jpeg" => BitmapEncoder.JpegEncoderId,
        "bmp" => BitmapEncoder.BmpEncoderId,
        "gif" => BitmapEncoder.GifEncoderId,
        "tiff" => BitmapEncoder.TiffEncoderId,
        _ => null,
    };

    /// <summary>Encodes to a WIC-supported format. <paramref name="quality"/> is 0..1 for lossy formats.</summary>
    public static async Task<bool> EncodeAsync(
        RasterImage image,
        string formatId,
        string outputPath,
        double quality,
        MetadataMode metadataMode,
        CancellationToken cancellationToken,
        int? scaledWidth = null,
        int? scaledHeight = null)
    {
        var encoderId = EncoderIdFor(formatId) ?? throw new ConversionException($"Windows Imaging cannot write {formatId.ToUpperInvariant()}.");
        var options = new BitmapPropertySet();
        if (formatId == "jpeg")
        {
            options.Add("ImageQuality", new BitmapTypedValue((float)Math.Clamp(quality, 0.05, 1.0), Windows.Foundation.PropertyType.Single));
        }
        else if (formatId == "tiff")
        {
            options.Add("TiffCompressionMethod", new BitmapTypedValue((byte)4 /* LZW */, Windows.Foundation.PropertyType.UInt8));
        }

        var source = formatId is "jpeg" or "bmp" ? image.FlattenOnto() : image;
        var alphaMode = formatId is "jpeg" or "bmp" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied;

        await using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream, options).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alphaMode, (uint)source.Width, (uint)source.Height, source.DpiX, source.DpiY, source.Pixels);
        if (scaledWidth is not null && scaledHeight is not null)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)scaledWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)scaledHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        var metadataWritten = await WriteMetadataAsync(encoder, image.Metadata, metadataMode);
        await encoder.FlushAsync().AsTask(cancellationToken);
        return metadataWritten;
    }

    public static (uint Width, uint Height) FitWithin(uint width, uint height, int? maxDimension)
    {
        if (maxDimension is not > 0 || (width <= maxDimension && height <= maxDimension))
        {
            return (width, height);
        }

        var scale = (double)maxDimension.Value / Math.Max(width, height);
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private static async Task<Dictionary<string, object>> ReadMetadataAsync(BitmapDecoder decoder)
    {
        var result = new Dictionary<string, object>();
        foreach (var name in MetadataProperties.Concat(LocationProperties))
        {
            try
            {
                var values = await decoder.BitmapProperties.GetPropertiesAsync([name]);
                if (values.TryGetValue(name, out var value) && value.Value is not null)
                {
                    result[name] = value.Value;
                }
            }
            catch (Exception)
            {
                // The source format does not support this property.
            }
        }

        return result;
    }

    private static async Task<bool> WriteMetadataAsync(BitmapEncoder encoder, IReadOnlyDictionary<string, object> metadata, MetadataMode mode)
    {
        if (mode == MetadataMode.Remove || metadata.Count == 0)
        {
            return true;
        }

        var written = 0;
        foreach (var (name, value) in metadata)
        {
            if (mode == MetadataMode.KeepWithoutLocation && name.StartsWith("System.GPS.", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var set = new BitmapPropertySet { { name, new BitmapTypedValue(value, TypeOf(value)) } };
                await encoder.BitmapProperties.SetPropertiesAsync(set);
                written++;
            }
            catch (Exception)
            {
                // The target format cannot store this property.
            }
        }

        return written > 0;
    }

    private static Windows.Foundation.PropertyType TypeOf(object value) => value switch
    {
        string => Windows.Foundation.PropertyType.String,
        string[] => Windows.Foundation.PropertyType.StringArray,
        double => Windows.Foundation.PropertyType.Double,
        double[] => Windows.Foundation.PropertyType.DoubleArray,
        float => Windows.Foundation.PropertyType.Single,
        uint => Windows.Foundation.PropertyType.UInt32,
        uint[] => Windows.Foundation.PropertyType.UInt32Array,
        int => Windows.Foundation.PropertyType.Int32,
        ushort => Windows.Foundation.PropertyType.UInt16,
        short => Windows.Foundation.PropertyType.Int16,
        byte => Windows.Foundation.PropertyType.UInt8,
        byte[] => Windows.Foundation.PropertyType.UInt8Array,
        DateTimeOffset => Windows.Foundation.PropertyType.DateTime,
        ulong => Windows.Foundation.PropertyType.UInt64,
        long => Windows.Foundation.PropertyType.Int64,
        _ => Windows.Foundation.PropertyType.Inspectable,
    };

    private static HashSet<string> LoadExtensions(IReadOnlyList<BitmapCodecInformation> codecs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var codec in codecs)
        {
            foreach (var extension in codec.FileExtensions)
            {
                set.Add(extension.Trim().ToLowerInvariant());
            }
        }

        return set;
    }
}

public enum MetadataMode
{
    Keep,
    KeepWithoutLocation,
    Remove,
}
