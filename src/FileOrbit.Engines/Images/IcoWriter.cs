namespace FileOrbit.Engines.Images;

/// <summary>
/// Writes a multi-resolution Windows .ico file whose entries are PNG-compressed images
/// (supported by Windows Vista and later).
/// </summary>
public static class IcoWriter
{
    public static readonly int[] StandardSizes = [16, 24, 32, 48, 64, 128, 256];

    public static void Write(string outputPath, IReadOnlyList<(int Size, byte[] Png)> entries)
    {
        if (entries.Count == 0 || entries.Count > 255)
        {
            throw new ArgumentException("An icon needs between 1 and 255 images.", nameof(entries));
        }

        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)entries.Count);

        var offset = 6 + 16 * entries.Count;
        foreach (var (size, png) in entries)
        {
            // ICONDIRENTRY: 0 means 256 for width/height.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // colour count
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in entries)
        {
            writer.Write(png);
        }
    }
}
