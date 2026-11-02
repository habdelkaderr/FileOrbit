using FileOrbit.Engines.Images;
using FileOrbit.Tests.Support;
using SkiaSharp;

namespace FileOrbit.Tests.Integration;

/// <summary>
/// The Skia-based codec used on macOS. Its Skia paths behave the same on every platform, so they are
/// tested here; the macOS-only paths (sips) are covered by the macOS build.
/// </summary>
public sealed class PortableImagingTests
{
    [Fact]
    public async Task Png_and_jpeg_round_trip()
    {
        using var dir = new TestDirectory();
        var source = dir.File("source.png");
        WriteHalves(source, 40, 20, SKEncodedImageFormat.Png);

        var image = await PortableImaging.DecodeAsync(source, null, TestContext.Current.CancellationToken);
        Assert.Equal((40, 20), (image.Width, image.Height));

        var png = dir.File("out.png");
        var jpeg = dir.File("out.jpg");
        Assert.True(await PortableImaging.EncodeAsync(image, "png", png, 1, TestContext.Current.CancellationToken));
        Assert.True(await PortableImaging.EncodeAsync(image, "jpeg", jpeg, 0.9, TestContext.Current.CancellationToken));

        var again = await PortableImaging.DecodeAsync(jpeg, null, TestContext.Current.CancellationToken);
        Assert.Equal((40, 20), (again.Width, again.Height));
        Assert.True(IsRed(ImageOps.PixelAt(again, 5, 10)));
        Assert.True(IsBlue(ImageOps.PixelAt(again, 35, 10)));
    }

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        using var dir = new TestDirectory();
        var path = dir.File("rotated.jpg");
        WriteHalves(path, 40, 20, SKEncodedImageFormat.Jpeg);
        InsertExifOrientation(path, orientation: 6); // "rotate 90° clockwise to display"

        var image = await PortableImaging.DecodeAsync(path, null, TestContext.Current.CancellationToken);

        Assert.Equal((20, 40), (image.Width, image.Height));
        Assert.True(IsRed(ImageOps.PixelAt(image, 10, 5)), "the left (red) half ends up on top");
        Assert.True(IsBlue(ImageOps.PixelAt(image, 10, 35)), "the right (blue) half ends up at the bottom");
    }

    [Fact]
    public async Task Downscales_to_the_requested_size()
    {
        using var dir = new TestDirectory();
        var path = dir.File("big.png");
        WriteHalves(path, 400, 200, SKEncodedImageFormat.Png);

        var image = await PortableImaging.DecodeAsync(path, 100, TestContext.Current.CancellationToken);

        Assert.Equal((100, 50), (image.Width, image.Height));
    }

    [Fact]
    public async Task Unreadable_files_are_reported_not_crashed()
    {
        using var dir = new TestDirectory();
        var path = dir.File("broken.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => PortableImaging.DecodeAsync(path, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Formats_needing_macOS_tools_are_not_offered_elsewhere()
    {
        Assert.True(PortableImaging.CanDecode(".webp"));
        Assert.True(PortableImaging.CanEncode("png"));
        Assert.Equal(PortableImaging.HasSips, PortableImaging.CanDecode(".heic"));
        Assert.Equal(PortableImaging.HasSips, PortableImaging.CanEncode("tiff"));
    }

    private static void WriteHalves(string path, int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(220, 20, 30));
            canvas.DrawRect(width / 2, 0, width / 2, height, new SKPaint { Color = new SKColor(20, 40, 220) });
        }

        using var data = bitmap.Encode(format, 95);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Adds a minimal EXIF block (just the orientation tag) right after the JPEG start marker.</summary>
    private static void InsertExifOrientation(string path, byte orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD at 8
            0x01, 0x00, // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, orientation, 0x00, 0x00, 0x00, // Orientation (SHORT)
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        var jpeg = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. jpeg[..2], .. exif, .. jpeg[2..]]);
    }

    private static bool IsRed((byte B, byte G, byte R, byte A) c) => c.R > 150 && c.B < 100;

    private static bool IsBlue((byte B, byte G, byte R, byte A) c) => c.B > 150 && c.R < 100;
}
