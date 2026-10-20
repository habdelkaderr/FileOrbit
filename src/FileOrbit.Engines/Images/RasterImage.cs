namespace FileOrbit.Engines.Images;

/// <summary>Decoded pixels in BGRA8, premultiplied alpha, orientation already applied.</summary>
public sealed class RasterImage
{
    public RasterImage(byte[] pixels, int width, int height, double dpiX = 96, double dpiY = 96)
    {
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Pixel buffer does not match the image size.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        DpiX = dpiX <= 1 ? 96 : dpiX;
        DpiY = dpiY <= 1 ? 96 : dpiY;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public double DpiX { get; }

    public double DpiY { get; }

    /// <summary>Number of frames/pages in the source (only the first is decoded).</summary>
    public int SourceFrameCount { get; init; } = 1;

    /// <summary>Metadata properties read from the source, keyed by Windows property name.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    public bool HasTransparency
    {
        get
        {
            for (var i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Returns a copy composited over an opaque background (for formats without alpha).</summary>
    public RasterImage FlattenOnto(byte red = 255, byte green = 255, byte blue = 255)
    {
        var result = new byte[Pixels.Length];
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var inverseAlpha = 255 - Pixels[i + 3];
            // Premultiplied "over": out = src + bg * (1 - a)
            result[i] = (byte)Math.Min(255, Pixels[i] + (blue * inverseAlpha + 127) / 255);
            result[i + 1] = (byte)Math.Min(255, Pixels[i + 1] + (green * inverseAlpha + 127) / 255);
            result[i + 2] = (byte)Math.Min(255, Pixels[i + 2] + (red * inverseAlpha + 127) / 255);
            result[i + 3] = 255;
        }

        return new RasterImage(result, Width, Height, DpiX, DpiY) { SourceFrameCount = SourceFrameCount, Metadata = Metadata };
    }

    /// <summary>Returns a copy centred on a transparent square canvas.</summary>
    public RasterImage PadToSquare()
    {
        if (Width == Height)
        {
            return this;
        }

        var size = Math.Max(Width, Height);
        var result = new byte[size * size * 4];
        var offsetX = (size - Width) / 2;
        var offsetY = (size - Height) / 2;
        for (var y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(Pixels, y * Width * 4, result, ((y + offsetY) * size + offsetX) * 4, Width * 4);
        }

        return new RasterImage(result, size, size, DpiX, DpiY);
    }
}
