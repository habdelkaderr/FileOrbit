using System.IO.Compression;
using System.Text;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed class FileTypeDetectorTests
{
    private readonly FileTypeDetector _detector = new(FormatCatalog.CreateDefault());

    public static TheoryData<string, byte[], string> Signatures => new()
    {
        { "a.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0], "png" },
        { "a.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10], "jpeg" },
        { "a.gif", "GIF89a\x01\x00"u8.ToArray(), "gif" },
        { "a.tif", "II*\0\x08\0\0\0"u8.ToArray(), "tiff" },
        { "a.bmp", [0x42, 0x4D, 1, 2, 3, 4, 0, 0, 0, 0, 0x36, 0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "bmp" },
        { "a.ico", [0, 0, 1, 0, 1, 0, 16, 16], "ico" },
        { "a.pdf", "%PDF-1.7\n"u8.ToArray(), "pdf" },
        { "a.rtf", "{\\rtf1\\ansi"u8.ToArray(), "rtf" },
        { "a.wav", "RIFF\x24\0\0\0WAVEfmt "u8.ToArray(), "wav" },
        { "a.webp", "RIFF\x24\0\0\0WEBPVP8 "u8.ToArray(), "webp" },
        { "a.avi", "RIFF\x24\0\0\0AVI LIST"u8.ToArray(), "avi" },
        { "a.mp4", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'], "mp4" },
        { "a.m4a", [0, 0, 0, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' ', 0, 0, 0, 0, (byte)'M', (byte)'4', (byte)'A', (byte)' '], "m4a" },
        { "a.mov", [0, 0, 0, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'q', (byte)'t', (byte)' ', (byte)' ', 0, 0, 0, 0, (byte)'q', (byte)'t', (byte)' ', (byte)' '], "mov" },
        { "a.heic", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0, (byte)'m', (byte)'i', (byte)'f', (byte)'1', (byte)'h', (byte)'e', (byte)'i', (byte)'c'], "heic" },
        { "a.avif", [0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0, (byte)'a', (byte)'v', (byte)'i', (byte)'f', (byte)'m', (byte)'i', (byte)'f', (byte)'1', (byte)'m', (byte)'i', (byte)'a', (byte)'f'], "avif" },
        { "a.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x88, (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a'], "mkv" },
        { "a.webm", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m'], "webm" },
        { "a.ogg", "OggS\0\x02\0\0\0\0\0\0\0\0\x01vorbis"u8.ToArray(), "ogg" },
        { "a.opus", "OggS\0\x02\0\0\0\0\0\0\0\0OpusHead"u8.ToArray(), "opus" },
        { "a.flac", "fLaC\0\0\0\x22"u8.ToArray(), "flac" },
        { "a.mp3", "ID3\x04\0\0\0\0\0\0"u8.ToArray(), "mp3" },
        { "b.mp3", [0xFF, 0xFB, 0x90, 0x64], "mp3" },
        { "a.aac", [0xFF, 0xF1, 0x50, 0x80], "aac" },
        { "a.wma", [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11], "wma" },
        { "a.wmv", [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11], "wmv" },
        { "a.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4], "7z" },
        { "a.rar", "Rar!\x1a\x07\x01\0"u8.ToArray(), "rar" },
        { "a.gz", [0x1F, 0x8B, 8, 0], "gz" },
        { "a.tar.gz", [0x1F, 0x8B, 8, 0], "tgz" },
        { "a.doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], "doc" },
        { "a.xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], "xls" },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Detects_format_from_signature(string name, byte[] header, string expected)
    {
        using var dir = new TestDirectory();
        var path = dir.Write(name, header.Concat(new byte[64]).ToArray());

        var detected = _detector.Detect(path);

        Assert.Equal(expected, detected.Format.Id);
        Assert.Equal(DetectionMethod.Signature, detected.Method);
    }

    [Fact]
    public void Content_wins_over_a_wrong_extension_and_warns()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("photo.jpg", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);

        var detected = _detector.Detect(path);

        Assert.Equal("png", detected.Format.Id);
        Assert.Contains("JPEG", detected.Warning);
    }

    [Fact]
    public void Renamed_file_is_not_trusted_by_extension()
    {
        using var dir = new TestDirectory();
        // A text file pretending to be a PDF must not be offered PDF conversions.
        var path = dir.Write("invoice.pdf", "just some text");

        var detected = _detector.Detect(path);

        Assert.NotEqual("pdf", detected.Format.Id);
    }

    [Theory]
    [InlineData("word/document.xml", "docx")]
    [InlineData("xl/workbook.xml", "xlsx")]
    [InlineData("ppt/presentation.xml", "pptx")]
    public void Ooxml_packages_are_identified_by_their_parts(string part, string expected)
    {
        using var dir = new TestDirectory();
        var path = dir.File("package.bin");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry(part);
        }

        var detected = _detector.Detect(path);

        Assert.Equal(expected, detected.Format.Id);
        Assert.Equal(DetectionMethod.Container, detected.Method);
    }

    [Theory]
    [InlineData("application/vnd.oasis.opendocument.text", "odt")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet", "ods")]
    [InlineData("application/vnd.oasis.opendocument.presentation", "odp")]
    public void OpenDocument_packages_are_identified_by_mimetype(string mimetype, string expected)
    {
        using var dir = new TestDirectory();
        var path = dir.File("doc.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), Encoding.ASCII);
            writer.Write(mimetype);
        }

        Assert.Equal(expected, _detector.Detect(path).Format.Id);
    }

    [Fact]
    public void Plain_zip_stays_zip()
    {
        using var dir = new TestDirectory();
        var path = SampleFiles.Zip(dir);
        Assert.Equal("zip", _detector.Detect(path).Format.Id);
    }

    [Theory]
    [InlineData("data.csv", "a,b\n1,2\n", "csv")]
    [InlineData("data.tsv", "a\tb\n1\t2\n", "tsv")]
    [InlineData("notes.md", "# Title\n", "md")]
    [InlineData("noext", "{\"a\": [1, 2]}", "json")]
    [InlineData("noext2", "<!DOCTYPE html><html></html>", "html")]
    [InlineData("drawing.xml", "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>", "svg")]
    [InlineData("readme", "Hello there", "txt")]
    public void Text_formats_are_sniffed(string name, string content, string expected)
    {
        using var dir = new TestDirectory();
        Assert.Equal(expected, _detector.Detect(dir.Write(name, content)).Format.Id);
    }

    [Fact]
    public void Utf16_text_is_text()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("u16.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo")).ToArray());
        Assert.Equal("txt", _detector.Detect(path).Format.Id);
    }

    [Fact]
    public void Unknown_binary_is_unknown()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("blob.bin", [0x00, 0x01, 0x02, 0x03, 0xFE, 0x00, 0x99]);
        Assert.Equal(KnownFormats.Unknown, _detector.Detect(path).Format);
    }

    [Fact]
    public void Empty_binary_extension_is_not_trusted()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("empty.png", Array.Empty<byte>());
        Assert.Equal(KnownFormats.Unknown, _detector.Detect(path).Format);
    }

    [Fact]
    public void Folder_is_detected_as_folder()
    {
        using var dir = new TestDirectory();
        var folder = dir.SubDirectory("Holiday Photos");
        var detected = _detector.Detect(folder);
        Assert.True(detected.IsDirectory);
        Assert.Equal("Holiday Photos", detected.BaseName);
    }

    [Fact]
    public void Compound_extension_base_name()
    {
        using var dir = new TestDirectory();
        var path = SampleFiles.TarGz(dir, "backup.2024.tar.gz");
        var detected = _detector.Detect(path);
        Assert.Equal("tgz", detected.Format.Id);
        Assert.Equal("backup.2024", detected.BaseName);
    }

    [Fact]
    public void Encrypted_office_package_keeps_its_office_format()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("secret.docx", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        Assert.Equal("docx", _detector.Detect(path).Format.Id);
    }
}
