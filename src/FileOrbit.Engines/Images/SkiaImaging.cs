using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FileOrbit.Core.Capabilities;
using SkiaSharp;
using Svg.Skia;

namespace FileOrbit.Engines.Images;

/// <summary>Skia-based helpers: WebP encoding, image-to-PDF, and SVG rendering.</summary>
public static partial class SkiaImaging
{
    /// <summary>Largest SVG we are willing to parse (SVG is XML and can be abused for memory exhaustion).</summary>
    public const long MaxSvgBytes = 50 * 1024 * 1024;

    public static void EncodeWebP(RasterImage image, string outputPath, int quality, bool lossless)
    {
        using var bitmap = ToBitmap(image);
        using var pixmap = bitmap.PeekPixels();
        var options = new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, lossless ? 100 : Math.Clamp(quality, 1, 100));
        using var data = pixmap.Encode(options) ?? throw new ConversionException("The WebP encoder failed.");
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        data.SaveTo(output);
    }

    /// <summary>Places the image on a single PDF page sized to the image at its native resolution.</summary>
    public static void WriteImagePdf(RasterImage image, string outputPath)
    {
        using var bitmap = ToBitmap(image);
        using var skImage = SKImage.FromBitmap(bitmap);
        var widthPoints = (float)(image.Width * 72.0 / image.DpiX);
        var heightPoints = (float)(image.Height * 72.0 / image.DpiY);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        using var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Producer = "FileOrbit", Creator = "FileOrbit" })
            ?? throw new ConversionException("Could not create the PDF document.");
        using (var canvas = document.BeginPage(widthPoints, heightPoints))
        {
            canvas.DrawImage(skImage, new SKRect(0, 0, widthPoints, heightPoints), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        document.EndPage();
        document.Close();
    }

    /// <summary>Renders an SVG to pixels. External references are removed before parsing.</summary>
    public static RasterImage RenderSvg(string path, int? targetWidth)
    {
        using var picture = LoadSvgPicture(path, out var svg);
        using (svg)
        {
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new ConversionException("The SVG has no drawable size.");
            }

            var scale = targetWidth is > 0 ? targetWidth.Value / bounds.Width : 1f;
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            if ((long)width * height > WindowsImaging.MaxPixels)
            {
                throw new ConversionException("The requested SVG output size is too large.");
            }

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            return new RasterImage(pixels, width, height);
        }
    }

    /// <summary>Writes an SVG into a one-page PDF, keeping it as vector graphics.</summary>
    public static void WriteSvgPdf(string path, string outputPath)
    {
        using var picture = LoadSvgPicture(path, out var svg);
        using (svg)
        {
            var bounds = picture.CullRect;
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
            using var document = SKDocument.CreatePdf(output) ?? throw new ConversionException("Could not create the PDF document.");
            using (var canvas = document.BeginPage(bounds.Width * 0.75f, bounds.Height * 0.75f))
            {
                canvas.Scale(0.75f); // CSS pixels (96/in) to PDF points (72/in)
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }

            document.EndPage();
            document.Close();
        }
    }

    private static SKPicture LoadSvgPicture(string path, out SKSvg svg)
    {
        if (new FileInfo(path).Length > MaxSvgBytes)
        {
            throw new ConversionException("The SVG file is too large to render safely.");
        }

        var text = SanitizeSvg(File.ReadAllText(path));
        svg = new SKSvg();
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            var picture = svg.Load(stream);
            if (picture is null)
            {
                throw new ConversionException("The SVG could not be rendered.");
            }

            // Keep the picture alive independently of the SKSvg instance.
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(picture.CullRect);
            canvas.DrawPicture(picture);
            return recorder.EndRecording();
        }
        catch (ConversionException)
        {
            svg.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            svg.Dispose();
            throw new ConversionException("The SVG could not be parsed.", ex.Message, ex);
        }
    }

    /// <summary>
    /// Rejects DTD/entity declarations (XXE, billion laughs) and removes references to external
    /// resources so rendering never touches the network or other local files.
    /// </summary>
    internal static string SanitizeSvg(string svg)
    {
        if (svg.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase) || svg.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) && svg.Contains('['))
        {
            throw new ConversionException("This SVG declares XML entities, which FileOrbit does not process for security reasons.");
        }

        return ExternalHref().Replace(svg, m => m.Groups["attr"].Value + "=\"#\"");
    }

    private static SKBitmap ToBitmap(RasterImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    // href / xlink:href values that are not fragment (#id) or inline data: URIs.
    [GeneratedRegex("""(?<attr>(?:xlink:)?href)\s*=\s*(?<q>["'])(?!\s*#|\s*data:)[^"']*\k<q>""", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalHref();
}
