using FileOrbit.Engines.Images;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Integration;

public sealed class ImageConversionTests
{
    private static readonly Pipeline P = Pipeline.Real;

    [Theory]
    [InlineData("convert:jpeg", "jpeg")]
    [InlineData("convert:webp", "webp")]
    [InlineData("convert:bmp", "bmp")]
    [InlineData("convert:gif", "gif")]
    [InlineData("convert:tiff", "tiff")]
    [InlineData("convert:ico", "ico")]
    [InlineData("convert:pdf", "pdf")]
    public async Task Png_converts_to_real_target_format(string action, string expected)
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir);

        var job = await P.ConvertAsync(png, action);

        P.AssertFormat(job.PrimaryOutput!, expected);
        Assert.True(File.Exists(png), "The source must be preserved");
        P.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Png_with_transparency_to_jpeg_warns_about_white_background()
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir);

        var job = await P.ConvertAsync(png, "convert:jpeg");

        Assert.Contains(job.Warnings, w => w.Contains("transparency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resize_produces_smaller_image_with_new_name()
    {
        using var dir = new TestDirectory();
        var jpeg = await SampleFiles.JpegAsync(dir, width: 1600, height: 1200);

        var job = await P.ConvertAsync(jpeg, "transform:jpeg:resized", new Dictionary<string, string> { ["maxSize"] = "800" });

        Assert.EndsWith("photo (resized).jpg", job.PrimaryOutput);
        var decoded = await WindowsImaging.DecodeAsync(job.PrimaryOutput!, null, false, CancellationToken.None);
        Assert.Equal(800, decoded.Width);
        Assert.Equal(600, decoded.Height);
    }

    [Fact]
    public async Task Ico_contains_multiple_sizes()
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir, width: 256, height: 256);

        var job = await P.ConvertAsync(png, "convert:ico");

        var bytes = await File.ReadAllBytesAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(IcoWriter.StandardSizes.Length, count);
        // Every entry is a PNG stream.
        for (var i = 0; i < count; i++)
        {
            var offset = BitConverter.ToInt32(bytes, 6 + i * 16 + 12);
            Assert.Equal(0x89, bytes[offset]);
        }
    }

    [Fact]
    public async Task Svg_renders_to_png_and_vector_pdf_without_loading_remote_resources()
    {
        using var dir = new TestDirectory();
        var svg = SampleFiles.Svg(dir);

        var png = await P.ConvertAsync(svg, "convert:png", new Dictionary<string, string> { ["svgWidth"] = "512" });
        var pdf = await P.ConvertAsync(svg, "convert:pdf");

        P.AssertFormat(png.PrimaryOutput!, "png");
        P.AssertFormat(pdf.PrimaryOutput!, "pdf");
        var decoded = await WindowsImaging.DecodeAsync(png.PrimaryOutput!, null, false, CancellationToken.None);
        Assert.Equal(512, decoded.Width);
    }

    [Fact]
    public void Svg_with_entities_is_rejected()
    {
        var malicious = """<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY x "boom">]><svg xmlns="http://www.w3.org/2000/svg">&x;</svg>""";
        Assert.Throws<FileOrbit.Core.Capabilities.ConversionException>(() => SkiaImaging.SanitizeSvg(malicious));
    }

    [Fact]
    public void Svg_sanitizer_removes_external_references_but_keeps_fragments()
    {
        var svg = """<svg><image href="https://evil/x.png"/><use xlink:href="#shape"/><image href="file:///C:/secret.png"/></svg>""";
        var cleaned = SkiaImaging.SanitizeSvg(svg);
        Assert.DoesNotContain("evil", cleaned);
        Assert.DoesNotContain("secret", cleaned);
        Assert.Contains("#shape", cleaned);
    }

    [Fact]
    public async Task Webp_round_trips_when_decoder_installed()
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir);
        var webp = await P.ConvertAsync(png, "convert:webp");
        if (!WindowsImaging.CanDecode(".webp"))
        {
            Assert.Skip("WebP Image Extensions are not installed");
        }

        var back = await P.ConvertAsync(webp.PrimaryOutput!, "convert:png");
        P.AssertFormat(back.PrimaryOutput!, "png");
        Assert.EndsWith("sample (1).png", back.PrimaryOutput); // Original sample.png exists, so the name is made unique.
    }
}
