using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileOrbit.App.Shell;

/// <summary>
/// Gets the same preview Explorer shows for a file (a real thumbnail for photos, PDFs and videos,
/// otherwise the file-type icon) through IShellItemImageFactory.
/// </summary>
public static partial class ShellThumbnail
{
    private const int SiigbfResizeToFit = 0x0;
    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfIconOnly = 0x4;

    /// <summary>Loads the preview on a background STA thread; returns null if none is available.</summary>
    public static Task<ImageSource?> GetAsync(string path, int size, bool iconOnly = false)
    {
        var completion = new TaskCompletionSource<ImageSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Get(path, size, iconOnly));
            }
            catch (Exception)
            {
                completion.SetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "FileOrbit thumbnail",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ImageSource? Get(string path, int size, bool iconOnly)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factoryObject) != 0 || factoryObject is not IShellItemImageFactory factory)
        {
            return null;
        }

        try
        {
            var flags = iconOnly ? SiigbfIconOnly : SiigbfBiggerSizeOk | SiigbfResizeToFit;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, flags, out var bitmap) != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // Shell bitmaps are 32-bit with alpha but are reported as opaque; reinterpret as BGRA.
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                result.Freeze();
                return result;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);
}
