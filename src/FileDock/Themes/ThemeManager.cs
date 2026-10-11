using System.Windows;
using System.Windows.Media;
using FileDock.Services;
using Microsoft.Win32;

namespace FileDock.Themes;

/// <summary>
/// Supplies the colour palette as dynamic brush resources (<c>Brush.*</c>). Follows the Windows
/// app light/dark preference, the accent colour and high-contrast mode, and updates live.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _resources;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Start(ResourceDictionary resources)
    {
        _resources = resources;
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Stop()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Apply();
                Changed?.Invoke();
            });
        }
    }

    private static void Apply()
    {
        if (_resources is null)
            return;

        // Developer override for checking each palette without changing Windows settings.
        string? forced = Environment.GetEnvironmentVariable("FILEDOCK_THEME")?.Trim().ToLowerInvariant();
        bool highContrast = forced is null ? SystemParameters.HighContrast : forced == "contrast";
        IsDark = !highContrast && (forced is null ? ReadAppsUseDarkTheme() : forced == "dark");
        Color accent = ReadAccentColor();
        Dictionary<string, Color> palette = highContrast ? HighContrast() : IsDark ? Dark(accent) : Light(accent);

        foreach ((string key, Color color) in palette)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _resources["Brush." + key] = brush;
            _resources["Color." + key] = color;
        }
    }

    private static Dictionary<string, Color> Light(Color accent)
    {
        Color panel = Hex("#FAFAFA");
        Color accentText = EnsureContrast(accent, panel, 4.5, toward: Colors.Black);
        return new()
        {
            ["Panel"] = panel,
            ["Text"] = Hex("#202020"),
            ["TextSecondary"] = Hex("#6B6B6B"),
            ["TextDisabled"] = Hex("#A0A0A0"),
            ["Separator"] = Hex("#E6E6E6"),
            ["Border"] = Hex("#CFCFCF"),
            ["Hover"] = Hex("#F0F0F0"),
            ["Pressed"] = Hex("#E8E8E8"),
            ["Selected"] = Blend(accent, panel, 0.14),
            ["SelectedHover"] = Blend(accent, panel, 0.20),
            ["SelectedText"] = Hex("#202020"),
            ["Accent"] = EnsureContrast(accent, panel, 3.0, toward: Colors.Black),
            ["AccentText"] = accentText,
            ["OnAccent"] = Colors.White,
            ["Error"] = Hex("#C42B1C"),
            ["Handle"] = Hex("#F3F3F3"),
            ["HandleBorder"] = Hex("#C8C8C8"),
            ["HandleGrip"] = Hex("#8A8A8A"),
            ["Focus"] = Hex("#202020"),
            ["ScrollThumb"] = Hex("#C4C4C4"),
            ["ScrollThumbHover"] = Hex("#9E9E9E"),
            ["Input"] = Hex("#FFFFFF"),
            ["InputBorder"] = Hex("#CCCCCC"),
            ["Overlay"] = panel,
        };
    }

    private static Dictionary<string, Color> Dark(Color accent)
    {
        Color panel = Hex("#202020");
        Color accentLight = EnsureContrast(accent, panel, 4.5, toward: Colors.White);
        return new()
        {
            ["Panel"] = panel,
            ["Text"] = Hex("#F2F2F2"),
            ["TextSecondary"] = Hex("#A8A8A8"),
            ["TextDisabled"] = Hex("#6E6E6E"),
            ["Separator"] = Hex("#333333"),
            ["Border"] = Hex("#454545"),
            ["Hover"] = Hex("#2D2D2D"),
            ["Pressed"] = Hex("#333333"),
            ["Selected"] = Blend(accent, panel, 0.26),
            ["SelectedHover"] = Blend(accent, panel, 0.32),
            ["SelectedText"] = Hex("#FFFFFF"),
            ["Accent"] = EnsureContrast(accent, panel, 3.0, toward: Colors.White),
            ["AccentText"] = accentLight,
            ["OnAccent"] = Relative(accentLight) > 0.4 ? Colors.Black : Colors.White,
            ["Error"] = Hex("#FF99A4"),
            ["Handle"] = Hex("#2B2B2B"),
            ["HandleBorder"] = Hex("#4A4A4A"),
            ["HandleGrip"] = Hex("#9A9A9A"),
            ["Focus"] = Hex("#FFFFFF"),
            ["ScrollThumb"] = Hex("#5A5A5A"),
            ["ScrollThumbHover"] = Hex("#7A7A7A"),
            ["Input"] = Hex("#2B2B2B"),
            ["InputBorder"] = Hex("#4A4A4A"),
            ["Overlay"] = panel,
        };
    }

    /// <summary>High-contrast mode uses the user's chosen system colours throughout.</summary>
    private static Dictionary<string, Color> HighContrast() => new()
    {
        ["Panel"] = SystemColors.WindowColor,
        ["Text"] = SystemColors.WindowTextColor,
        ["TextSecondary"] = SystemColors.WindowTextColor,
        ["TextDisabled"] = SystemColors.GrayTextColor,
        ["Separator"] = SystemColors.WindowTextColor,
        ["Border"] = SystemColors.WindowTextColor,
        ["Hover"] = SystemColors.HighlightColor,
        ["Pressed"] = SystemColors.HighlightColor,
        ["Selected"] = SystemColors.HighlightColor,
        ["SelectedHover"] = SystemColors.HighlightColor,
        ["SelectedText"] = SystemColors.HighlightTextColor,
        ["Accent"] = SystemColors.HighlightColor,
        ["AccentText"] = SystemColors.HotTrackColor,
        ["OnAccent"] = SystemColors.HighlightTextColor,
        ["Error"] = SystemColors.WindowTextColor,
        ["Handle"] = SystemColors.WindowColor,
        ["HandleBorder"] = SystemColors.WindowTextColor,
        ["HandleGrip"] = SystemColors.WindowTextColor,
        ["Focus"] = SystemColors.WindowTextColor,
        ["ScrollThumb"] = SystemColors.WindowTextColor,
        ["ScrollThumbHover"] = SystemColors.HighlightColor,
        ["Input"] = SystemColors.WindowColor,
        ["InputBorder"] = SystemColors.WindowTextColor,
        ["Overlay"] = SystemColors.WindowColor,
    };

    private static bool ReadAppsUseDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn("Could not read the app theme preference", ex);
            return false;
        }
    }

    /// <summary>The Windows accent colour (DWM stores it as 0xAABBGGRR), or a standard blue.</summary>
    private static Color ReadAccentColor()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                uint v = unchecked((uint)abgr);
                return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn("Could not read the accent colour", ex);
        }
        return Hex("#0067C0");
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Blend(Color top, Color bottom, double amount) => Color.FromRgb(
        (byte)Math.Round(top.R * amount + bottom.R * (1 - amount)),
        (byte)Math.Round(top.G * amount + bottom.G * (1 - amount)),
        (byte)Math.Round(top.B * amount + bottom.B * (1 - amount)));

    /// <summary>Moves a colour toward black or white until it reaches the requested contrast ratio.</summary>
    internal static Color EnsureContrast(Color color, Color background, double ratio, Color toward)
    {
        Color current = color;
        for (int step = 1; step <= 20 && Contrast(current, background) < ratio; step++)
            current = Blend(toward, color, step * 0.05);
        return current;
    }

    internal static double Contrast(Color a, Color b)
    {
        double la = Relative(a), lb = Relative(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Relative(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
