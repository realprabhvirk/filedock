using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileDock.Native;

namespace FileDock.Services;

/// <summary>
/// Supplies Windows shell icons (not thumbnails) for shelf rows. Icons are requested on a
/// single background STA thread, so slow or offline paths never block the UI, and are cached by
/// file type so a shelf of a hundred PDFs needs one lookup.
/// </summary>
public sealed class IconService : IDisposable
{
    // File types whose icon is specific to the individual file rather than the extension.
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".url", ".appref-ms", ".msc", ".scr", ".cpl", ".dll",
    };

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;

    public IconService()
    {
        _worker = new Thread(() =>
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "FileDock icon loader",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Requests an icon; <paramref name="apply"/> is invoked on the worker thread with a frozen image.</summary>
    public void Request(string path, bool isFolder, bool available, int pixelSize, Action<ImageSource?> apply)
    {
        string key = CacheKey(path, isFolder, available, pixelSize);
        if (_cache.TryGetValue(key, out ImageSource? cached))
        {
            apply(cached);
            return;
        }

        if (_queue.IsAddingCompleted)
            return;
        _queue.Add(() =>
        {
            ImageSource? image = _cache.GetOrAdd(key, _ => Load(path, isFolder, available, pixelSize));
            apply(image);
        });
    }

    private static string CacheKey(string path, bool isFolder, bool available, int size)
    {
        string state = available ? "ok" : "missing";
        if (isFolder)
        {
            // Drive roots and shares have their own icons; ordinary folders share one.
            bool isRoot = Path.GetPathRoot(path)?.TrimEnd('\\').Length >= path.TrimEnd('\\').Length;
            return $"{state}|{size}|{(isRoot && available ? path : "<folder>")}";
        }
        string ext = Path.GetExtension(path);
        if (available && PerFileExtensions.Contains(ext))
            return $"{state}|{size}|{path}";
        return $"{state}|{size}|{(ext.Length == 0 ? "<noext>" : ext)}";
    }

    private static ImageSource? Load(string path, bool isFolder, bool available, int size)
    {
        try
        {
            if (available)
            {
                ImageSource? image = LoadFromShellItem(path, size);
                if (image is not null)
                    return image;
            }
            return LoadByType(path, isFolder);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or ExternalException)
        {
            Log.Warn($"Icon lookup failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static ImageSource? LoadFromShellItem(string path, int size)
    {
        if (ShellInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ShellInterop.IID_IShellItemImageFactory, out object? item) != 0
            || item is not ShellInterop.IShellItemImageFactory factory)
            return null;

        try
        {
            if (factory.GetImage(new ShellInterop.SIZE(size, size), ShellInterop.SIIGBF.IconOnly, out IntPtr hbitmap) != 0
                || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                return FromHBitmap(hbitmap);
            }
            finally
            {
                Win32.DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(item);
        }
    }

    /// <summary>Generic icon for the file type, without touching the disk (used for unavailable items).</summary>
    private static ImageSource? LoadByType(string path, bool isFolder)
    {
        var info = new ShellInterop.SHFILEINFO();
        string name = isFolder ? "folder" : Path.GetFileName(path);
        uint attributes = isFolder ? ShellInterop.FILE_ATTRIBUTE_DIRECTORY : ShellInterop.FILE_ATTRIBUTE_NORMAL;
        IntPtr result = ShellInterop.SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShellInterop.SHFILEINFO>(),
            ShellInterop.SHGFI_ICON | ShellInterop.SHGFI_LARGEICON | ShellInterop.SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>
    /// Copies a 32-bpp premultiplied DIB section into a WPF bitmap. Imaging.CreateBitmapSourceFromHBitmap
    /// discards the alpha channel, which would draw icons on black squares.
    /// </summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        int written = ShellInterop.GetObject(hbitmap, Marshal.SizeOf<ShellInterop.DIBSECTION>(), out ShellInterop.DIBSECTION dib);
        ShellInterop.BITMAP bm = dib.dsBm;
        if (written == 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            BitmapSource fallback = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int stride = bm.bmWidthBytes;
        var pixels = new byte[stride * bm.bmHeight];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        // A positive biHeight means the DIB is stored bottom-up.
        if (dib.dsBmih.biHeight > 0)
        {
            var row = new byte[stride];
            for (int top = 0, bottom = bm.bmHeight - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }

        // Some legacy icons carry no alpha at all; treat them as opaque instead of invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        BitmapSource bitmap = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
