using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Detection;

/// <summary>
/// Determines a file's real format from its content. The extension is only used to break ties
/// between formats that share a container (e.g. legacy OLE documents) or for plain text.
/// </summary>
public sealed class FileTypeDetector
{
    private const int HeaderSize = 4096;
    private readonly FormatCatalog _catalog;

    public FileTypeDetector(FormatCatalog catalog) => _catalog = catalog;

    public DetectedFile Detect(string path)
    {
        if (Directory.Exists(path))
        {
            return new DetectedFile(path, KnownFormats.Folder, DetectionMethod.Directory, 0);
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The file does not exist.", path);
        }

        var header = new byte[HeaderSize];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        var span = header.AsSpan(0, read);
        var byExtension = _catalog.FindByFileName(path);
        if (read == 0 && byExtension is not { IsText: true })
        {
            return new DetectedFile(path, KnownFormats.Unknown, DetectionMethod.Unknown, 0, "The file is empty.");
        }

        var (format, method) = DetectFromContent(path, span, byExtension);
        format ??= byExtension;
        if (format is null)
        {
            return new DetectedFile(path, KnownFormats.Unknown, DetectionMethod.Unknown, info.Length);
        }

        if (method == DetectionMethod.Unknown)
        {
            method = DetectionMethod.Extension;
        }

        string? warning = null;
        if (byExtension is not null && byExtension != format && !AreCompatible(byExtension, format))
        {
            warning = $"The file is named as {byExtension.Name}, but its content is {format.Name}. FileOrbit will treat it as {format.Name}.";
        }
        else if (byExtension is null && method is DetectionMethod.Signature or DetectionMethod.Container)
        {
            warning = $"Unrecognised extension; content identified as {format.Name}.";
        }

        return new DetectedFile(path, format, method, info.Length, warning);
    }

    private (FileFormat? Format, DetectionMethod Method) DetectFromContent(string path, ReadOnlySpan<byte> h, FileFormat? byExtension)
    {
        if (h.Length == 0)
        {
            // An empty file has no content to verify; only plain text formats can be "empty but valid".
            return byExtension is { IsText: true } ? (byExtension, DetectionMethod.Extension) : (null, DetectionMethod.Unknown);
        }

        var signature = DetectSignature(h, byExtension);
        if (signature is not null)
        {
            if (signature == KnownFormats.Zip)
            {
                var inner = InspectZipContainer(path);
                if (inner is not null)
                {
                    return (inner, DetectionMethod.Container);
                }
            }

            return (signature, DetectionMethod.Signature);
        }

        var text = SniffText(h, byExtension);
        if (text is not null)
        {
            return (text, DetectionMethod.Content);
        }

        return (null, DetectionMethod.Unknown);
    }

    internal static FileFormat? DetectSignature(ReadOnlySpan<byte> h, FileFormat? byExtension)
    {
        static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) => data.StartsWith(prefix);
        static bool At(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> value) =>
            data.Length >= offset + value.Length && data.Slice(offset, value.Length).SequenceEqual(value);

        if (StartsWith(h, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return KnownFormats.Png;
        if (StartsWith(h, [0xFF, 0xD8, 0xFF])) return KnownFormats.Jpeg;
        if (StartsWith(h, "GIF87a"u8) || StartsWith(h, "GIF89a"u8)) return KnownFormats.Gif;
        if (StartsWith(h, "II*\0"u8) || StartsWith(h, "MM\0*"u8)) return KnownFormats.Tiff;
        if (StartsWith(h, "BM"u8) && h.Length >= 26 && At(h, 6, [0, 0, 0, 0])) return KnownFormats.Bmp;
        if (StartsWith(h, [0x00, 0x00, 0x01, 0x00]) && h.Length >= 6 && (h[4] | h[5]) != 0) return KnownFormats.Ico;
        if (IndexOf(h, "%PDF-"u8, 1024) >= 0) return KnownFormats.Pdf;
        if (StartsWith(h, "{\\rtf"u8)) return KnownFormats.Rtf;

        if (StartsWith(h, "RIFF"u8) && h.Length >= 12)
        {
            if (At(h, 8, "WAVE"u8)) return KnownFormats.Wav;
            if (At(h, 8, "WEBP"u8)) return KnownFormats.WebP;
            if (At(h, 8, "AVI "u8)) return KnownFormats.Avi;
        }

        if (h.Length >= 12 && At(h, 4, "ftyp"u8))
        {
            return DetectIsoMedia(h);
        }

        if (StartsWith(h, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return IndexOf(h, "webm"u8, 64) >= 0 ? KnownFormats.WebM : KnownFormats.Mkv;
        }

        if (StartsWith(h, "OggS"u8))
        {
            return IndexOf(h, "OpusHead"u8, 128) >= 0 ? KnownFormats.Opus : KnownFormats.Ogg;
        }

        if (StartsWith(h, "fLaC"u8)) return KnownFormats.Flac;
        if (StartsWith(h, "ID3"u8)) return KnownFormats.Mp3;
        if (h.Length >= 2 && h[0] == 0xFF)
        {
            // MPEG audio frame sync: 11 set bits. Layer bits distinguish MP3 (layer III) from ADTS AAC (layer 00).
            if ((h[1] & 0xE0) == 0xE0 && (h[1] & 0x06) == 0x02) return KnownFormats.Mp3;
            if ((h[1] & 0xF6) == 0xF0) return KnownFormats.Aac;
        }

        if (StartsWith(h, [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11]))
        {
            return byExtension == KnownFormats.Wma ? KnownFormats.Wma : KnownFormats.Wmv;
        }

        if (StartsWith(h, "PK\x03\x04"u8) || StartsWith(h, "PK\x05\x06"u8)) return KnownFormats.Zip;
        if (StartsWith(h, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return KnownFormats.SevenZip;
        if (StartsWith(h, "Rar!\x1a\x07"u8)) return KnownFormats.Rar;
        if (StartsWith(h, [0x1F, 0x8B])) return byExtension == KnownFormats.TarGz ? KnownFormats.TarGz : KnownFormats.Gz;
        if (At(h, 257, "ustar"u8)) return KnownFormats.Tar;

        if (StartsWith(h, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            // OLE compound file: the container of .doc, .xls and .ppt, and also of password-encrypted
            // .docx/.xlsx/.pptx files. The extension tells them apart.
            return byExtension is { Id: "doc" or "xls" or "ppt" or "docx" or "xlsx" or "pptx" } ? byExtension : null;
        }

        return null;
    }

    private static FileFormat DetectIsoMedia(ReadOnlySpan<byte> h)
    {
        var boxSize = (int)Math.Min(h.Length, Math.Max(16, (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3]));
        var majorBrand = Encoding.ASCII.GetString(h.Slice(8, 4));
        var brands = new List<string> { majorBrand };
        for (var offset = 16; offset + 4 <= boxSize; offset += 4)
        {
            brands.Add(Encoding.ASCII.GetString(h.Slice(offset, 4)));
        }

        if (brands.Any(b => b is "avif" or "avis")) return KnownFormats.Avif;
        if (brands.Any(b => b is "heic" or "heix" or "hevc" or "heim" or "heis" or "hevx" or "mif1" or "msf1")) return KnownFormats.Heic;
        if (majorBrand == "qt  ") return KnownFormats.Mov;
        if (majorBrand is "M4A " or "M4B " or "M4P ") return KnownFormats.M4a;
        return KnownFormats.Mp4;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value, int limit)
    {
        var window = data[..Math.Min(data.Length, limit + value.Length)];
        return window.IndexOf(value);
    }

    /// <summary>Distinguishes OOXML and OpenDocument packages from plain ZIP archives.</summary>
    internal static FileFormat? InspectZipContainer(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var mimetype = archive.GetEntry("mimetype");
            if (mimetype is not null && mimetype.Length < 256)
            {
                using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
                var value = reader.ReadToEnd().Trim();
                return value switch
                {
                    "application/vnd.oasis.opendocument.text" => KnownFormats.Odt,
                    "application/vnd.oasis.opendocument.spreadsheet" => KnownFormats.Ods,
                    "application/vnd.oasis.opendocument.presentation" => KnownFormats.Odp,
                    _ => null,
                };
            }

            if (archive.GetEntry("[Content_Types].xml") is null)
            {
                return null;
            }

            if (archive.GetEntry("word/document.xml") is not null) return KnownFormats.Docx;
            if (archive.GetEntry("xl/workbook.xml") is not null) return KnownFormats.Xlsx;
            if (archive.GetEntry("ppt/presentation.xml") is not null) return KnownFormats.Pptx;
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static FileFormat? SniffText(ReadOnlySpan<byte> h, FileFormat? byExtension)
    {
        if (!LooksLikeText(h, out var text))
        {
            return null;
        }

        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        var head = trimmed.Length > 512 ? trimmed[..512] : trimmed;

        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return KnownFormats.Svg;
        }

        if (byExtension is { IsText: true } && byExtension != KnownFormats.Svg)
        {
            return byExtension;
        }

        if (head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return KnownFormats.Html;
        }

        if ((head.StartsWith('{') || head.StartsWith('[')) && IsCompleteJson(h))
        {
            return KnownFormats.Json;
        }

        return KnownFormats.Txt;
    }

    private static bool IsCompleteJson(ReadOnlySpan<byte> h)
    {
        if (h.Length >= HeaderSize)
        {
            return true; // Too large to validate cheaply; the leading bracket is a strong enough hint.
        }

        try
        {
            using var _ = JsonDocument.Parse(h.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool LooksLikeText(ReadOnlySpan<byte> h, out string text)
    {
        text = string.Empty;
        if (h.Length >= 2 && ((h[0] == 0xFF && h[1] == 0xFE) || (h[0] == 0xFE && h[1] == 0xFF)))
        {
            var encoding = h[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            text = encoding.GetString(h[2..]);
            return true;
        }

        var controlCount = 0;
        foreach (var b in h)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
            {
                controlCount++;
            }
        }

        if (controlCount > h.Length / 100 + 1)
        {
            return false;
        }

        // Accept a header that is valid UTF-8 apart from a sequence cut off at the end of the buffer.
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true);
        for (var trim = 0; trim < 4 && trim < h.Length; trim++)
        {
            try
            {
                text = decoder.GetString(h[..^trim]);
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        // Fall back to legacy single-byte text (e.g. Windows-1252) when there are no control characters.
        text = Encoding.Latin1.GetString(h);
        return controlCount == 0;
    }

    private static bool AreCompatible(FileFormat extension, FileFormat content) =>
        (extension.Id, content.Id) switch
        {
            ("tgz", "gz") or ("gz", "tgz") => true,
            ("m4a", "mp4") or ("mp4", "m4a") or ("mov", "mp4") or ("mp4", "mov") => true,
            ("aac", "m4a") => true,
            ("heic", "avif") => false,
            ("txt", _) when content.IsText => true,
            (_, "txt") when extension.IsText => true,
            _ => false,
        };
}
