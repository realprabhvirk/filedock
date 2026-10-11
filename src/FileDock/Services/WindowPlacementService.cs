using System.Runtime.InteropServices;
using FileDock.Models;
using FileDock.Native;

namespace FileDock.Services;

/// <summary>A rectangle in physical (device) pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public sealed record DisplayInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary, double Scale);

/// <summary>
/// Where the dock window and its handle go. The window always has the expanded panel's size;
/// <see cref="Handle"/> is the strip (relative to the window) that stays visible when collapsed.
/// </summary>
public sealed record DockLayout(PixelRect Window, PixelRect Handle, double Scale);

public static class WindowPlacementService
{
    // Sizes in device-independent pixels (1/96 inch).
    public const double HandleWidth = 10;
    public const double HandleHeight = 64;
    public const double PanelWidth = 340;
    public const double PanelHeight = 480;
    public const double MinPanelHeight = 220;
    public const double ScreenMargin = 8;

    /// <summary>
    /// Computes the dock layout inside a monitor's work area (which already excludes the taskbar).
    /// The panel is centred on the handle where possible and always kept inside the work area,
    /// so expanding never moves the handle out from under the pointer.
    /// </summary>
    public static DockLayout Compute(PixelRect work, double scale, DockEdge edge, double verticalFraction)
    {
        int Px(double dip) => (int)Math.Round(dip * scale);

        int margin = Px(ScreenMargin);
        int width = Math.Min(Px(PanelWidth), work.Width);
        int availableHeight = Math.Max(1, work.Height - 2 * margin);
        int height = Math.Min(Px(PanelHeight), availableHeight);
        height = Math.Max(height, Math.Min(Px(MinPanelHeight), work.Height));

        int handleWidth = Px(HandleWidth);
        int handleHeight = Math.Min(Px(HandleHeight), height);

        double fraction = double.IsFinite(verticalFraction) ? Math.Clamp(verticalFraction, 0, 1) : 0.5;
        int minCenter = work.Y + margin + handleHeight / 2;
        int maxCenter = work.Bottom - margin - handleHeight / 2;
        if (maxCenter < minCenter)
            minCenter = maxCenter = work.Y + work.Height / 2;
        int handleCenter = minCenter + (int)Math.Round((maxCenter - minCenter) * fraction);
        int handleTop = handleCenter - handleHeight / 2;

        int lowest = work.Y + margin;
        int highest = work.Bottom - margin - height;
        if (highest < lowest)
            lowest = highest = work.Y + (work.Height - height) / 2;
        int windowTop = Math.Clamp(handleCenter - height / 2, lowest, highest);
        // The handle must lie inside the window so the drop target is continuous while expanding.
        windowTop = Math.Clamp(windowTop, handleTop + handleHeight - height, handleTop);

        int windowLeft = edge == DockEdge.Right ? work.Right - width : work.X;
        var window = new PixelRect(windowLeft, windowTop, width, height);

        int handleX = edge == DockEdge.Right ? width - handleWidth : 0;
        var handle = new PixelRect(handleX, handleTop - windowTop, handleWidth, handleHeight);

        return new DockLayout(window, handle, scale);
    }

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            DisplayInfo? info = Describe(monitor);
            if (info is not null)
                displays.Add(info);
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    /// <summary>Returns the requested display, or the primary display if it is no longer connected.</summary>
    public static DisplayInfo? Resolve(string? deviceName)
    {
        IReadOnlyList<DisplayInfo> displays = GetDisplays();
        return displays.FirstOrDefault(d => deviceName is not null && d.DeviceName == deviceName)
               ?? displays.FirstOrDefault(d => d.IsPrimary)
               ?? displays.FirstOrDefault();
    }

    private static DisplayInfo? Describe(IntPtr monitor)
    {
        var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
            return null;

        double scale = 1.0;
        if (Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        static PixelRect ToRect(Win32.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
        return new DisplayInfo(info.szDevice, ToRect(info.rcMonitor), ToRect(info.rcWork),
            (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0, scale);
    }
}
