using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Images;
using FileOrbit.Tests.Support;
using SkiaSharp;

namespace FileOrbit.Tests.Integration;

public sealed class ImageEditTests
{
    private static readonly Pipeline P = Pipeline.Real;

    private static async Task<RasterImage> Read(string path) =>
        await WindowsImaging.DecodeAsync(path, null, false, CancellationToken.None);

    [Theory]
    [InlineData("longest", 800, 0, 0, 0, "contain", false, 800, 600)]
    [InlineData("width", 400, 0, 0, 0, "contain", false, 400, 300)]
    [InlineData("height", 300, 0, 0, 0, "contain", false, 400, 300)]
    [InlineData("percent", 0, 0, 0, 25, "contain", false, 400, 300)]
    [InlineData("exact", 0, 500, 500, 0, "contain", false, 500, 375)]
    [InlineData("exact", 0, 500, 500, 0, "cover", false, 500, 500)]
    [InlineData("exact", 0, 500, 200, 0, "stretch", false, 500, 200)]
    [InlineData("longest", 3200, 0, 0, 0, "contain", false, 1600, 1200)] // never enlarged by default
    [InlineData("longest", 3200, 0, 0, 0, "contain", true, 3200, 2400)]
    public async Task Resize_modes_produce_expected_dimensions(string mode, int size, int width, int height, int percent, string fit, bool upscale, int expectedWidth, int expectedHeight)
    {
        using var dir = new TestDirectory();
        var jpeg = await SampleFiles.JpegAsync(dir, width: 1600, height: 1200);

        var job = await P.ConvertAsync(jpeg, "transform:jpeg:resized", new Dictionary<string, string>
        {
            ["mode"] = mode,
            ["size"] = size.ToString(),
            ["width"] = width.ToString(),
            ["height"] = height.ToString(),
            ["percent"] = percent.ToString(),
            ["fit"] = fit,
            ["upscale"] = upscale ? "true" : "false",
        });

        var result = await Read(job.PrimaryOutput!);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.EndsWith("photo (resized).jpg", job.PrimaryOutput);
    }

    [Theory]
    [InlineData("right", 48, 64)]
    [InlineData("left", 48, 64)]
    [InlineData("180", 64, 48)]
    [InlineData("fliph", 64, 48)]
    public async Task Rotate_and_flip(string rotate, int width, int height)
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir);

        var job = await P.ConvertAsync(png, "transform:png:rotated", new Dictionary<string, string> { ["rotate"] = rotate });

        var result = await Read(job.PrimaryOutput!);
        Assert.Equal((width, height), (result.Width, result.Height));
    }

    [Fact]
    public void Rotation_moves_pixels_correctly()
    {
        // 2×1 image: left pixel red, right pixel blue (premultiplied BGRA).
        var image = new RasterImage([0, 0, 255, 255, 255, 0, 0, 255], 2, 1);

        var clockwise = ImageOps.RotateClockwise(image);
        Assert.Equal((1, 2), (clockwise.Width, clockwise.Height));
        Assert.Equal(255, ImageOps.PixelAt(clockwise, 0, 0).R); // red ends up on top
        Assert.Equal(255, ImageOps.PixelAt(clockwise, 0, 1).B);

        var mirrored = ImageOps.FlipHorizontal(image);
        Assert.Equal(255, ImageOps.PixelAt(mirrored, 0, 0).B);
    }

    [Fact]
    public async Task Crop_to_square_and_widescreen()
    {
        using var dir = new TestDirectory();
        var jpeg = await SampleFiles.JpegAsync(dir, width: 1600, height: 1200);

        var square = await P.ConvertAsync(jpeg, "transform:jpeg:cropped", new Dictionary<string, string> { ["aspect"] = "1:1" });
        var wide = await P.ConvertAsync(jpeg, "transform:jpeg:cropped", new Dictionary<string, string> { ["aspect"] = "16:9", ["anchor"] = "top" });

        var squareImage = await Read(square.PrimaryOutput!);
        var wideImage = await Read(wide.PrimaryOutput!);
        Assert.Equal((1200, 1200), (squareImage.Width, squareImage.Height));
        Assert.Equal((1600, 900), (wideImage.Width, wideImage.Height));
    }

    [Fact]
    public async Task Trim_removes_plain_border()
    {
        using var dir = new TestDirectory();
        var path = dir.File("framed.png");
        using (var bitmap = new SKBitmap(200, 100))
        {
            bitmap.Erase(SKColors.White);
            using var canvas = new SKCanvas(bitmap);
            canvas.DrawRect(new SKRect(50, 20, 150, 80), new SKPaint { Color = SKColors.DarkGreen });
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, data.ToArray(), TestContext.Current.CancellationToken);
        }

        var job = await P.ConvertAsync(path, "transform:png:trimmed");

        var result = await Read(job.PrimaryOutput!);
        Assert.Equal((100, 60), (result.Width, result.Height));
    }

    [Theory]
    [InlineData("gray")]
    [InlineData("sepia")]
    [InlineData("contrast")]
    public async Task Black_and_white_styles(string style)
    {
        using var dir = new TestDirectory();
        var png = await SampleFiles.PngAsync(dir);

        var job = await P.ConvertAsync(png, "transform:png:monochrome", new Dictionary<string, string> { ["style"] = style });

        var result = await Read(job.PrimaryOutput!);
        var (b, g, r, _) = ImageOps.PixelAt(result, 40, 20);
        if (style == "sepia")
        {
            Assert.True(r >= g && g >= b, "Sepia is warm: red >= green >= blue");
        }
        else
        {
            Assert.True(Math.Abs(r - g) <= 1 && Math.Abs(g - b) <= 1, "Black and white has equal channels");
        }

        Assert.EndsWith("sample (black and white).png", job.PrimaryOutput);
    }

    [Fact]
    public void Auto_enhance_stretches_a_dull_image()
    {
        // A low-contrast grey ramp between 100 and 150.
        var pixels = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var v = (byte)(100 + i * 50 / 255);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = 255;
        }

        var enhanced = ImageOps.AutoEnhance(new RasterImage(pixels, 256, 1));

        Assert.True(ImageOps.PixelAt(enhanced, 0, 0).R < 10);
        Assert.True(ImageOps.PixelAt(enhanced, 255, 0).R > 245);
    }

    [Fact]
    public async Task Compress_to_size_limit()
    {
        using var dir = new TestDirectory();
        var path = dir.File("noisy.jpg");
        using (var bitmap = new SKBitmap(2400, 1800))
        {
            var random = new Random(42);
            var bytes = new byte[2400 * 1800 * 4];
            random.NextBytes(bytes);
            for (var i = 3; i < bytes.Length; i += 4)
            {
                bytes[i] = 255;
            }

            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            await File.WriteAllBytesAsync(path, data.ToArray(), TestContext.Current.CancellationToken);
        }

        Assert.True(new FileInfo(path).Length > 1024 * 1024, "sample should start well above the limit");

        var job = await P.ConvertAsync(path, "transform:jpeg:compressed", new Dictionary<string, string> { ["goal"] = "max200" });

        Assert.InRange(new FileInfo(job.PrimaryOutput!).Length, 1, 200 * 1024);
        P.AssertFormat(job.PrimaryOutput!, "jpeg");
    }

    [Fact]
    public async Task Background_removal_keeps_subject_and_clears_background()
    {
        using var dir = new TestDirectory();
        var path = dir.File("product.jpg");
        using (var bitmap = new SKBitmap(640, 480))
        {
            bitmap.Erase(new SKColor(235, 238, 240));
            using var canvas = new SKCanvas(bitmap);
            canvas.DrawCircle(320, 240, 150, new SKPaint { Color = new SKColor(200, 30, 40), IsAntialias = true });
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
            await File.WriteAllBytesAsync(path, data.ToArray(), TestContext.Current.CancellationToken);
        }

        var job = await P.ConvertAsync(path, "transform:png:nobg");

        Assert.EndsWith("product (no background).png", job.PrimaryOutput);
        var result = await Read(job.PrimaryOutput!);
        Assert.Equal((640, 480), (result.Width, result.Height));
        Assert.True(ImageOps.PixelAt(result, 320, 240).A > 200, "the subject stays opaque");
        Assert.True(ImageOps.PixelAt(result, 10, 10).A < 60, "the background becomes transparent");
    }

    [Fact]
    public void Options_show_only_when_relevant()
    {
        var engine = new ImageEditEngine();
        var resize = engine.Capabilities.First(c => c.Variant == "resized");
        var pixels = resize.Options.Single(o => o.Key == "size");
        var exactWidth = resize.Options.Single(o => o.Key == "width");

        var byWidth = new Dictionary<string, string> { ["mode"] = "width" };
        var exact = new Dictionary<string, string> { ["mode"] = "exact" };
        Assert.True(pixels.IsVisible(byWidth));
        Assert.False(exactWidth.IsVisible(byWidth));
        Assert.True(exactWidth.IsVisible(exact));
        Assert.False(pixels.IsVisible(exact));
    }
}
