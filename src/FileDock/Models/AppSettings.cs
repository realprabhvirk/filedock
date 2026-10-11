namespace FileDock.Models;

public enum DockEdge
{
    Right,
    Left,
}

/// <summary>Contents of <c>settings.json</c>.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const string DefaultHotkey = "Ctrl+Alt+Space";

    /// <summary>Used once, automatically, if another app already owns the default shortcut.</summary>
    public const string FallbackHotkey = "Ctrl+Alt+Shift+Space";

    /// <summary>Close-delay choices offered in Settings, in milliseconds.</summary>
    public static readonly IReadOnlyList<int> CollapseDelayChoices = [600, 900, 1500];

    public int Version { get; set; } = CurrentVersion;

    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>Handle position from the top (0) to the bottom (1) of the work area.</summary>
    public double VerticalPosition { get; set; } = 0.5;

    /// <summary>Monitor device name (for example <c>\\.\DISPLAY2</c>); null means the primary display.</summary>
    public string? Display { get; set; }

    public int CollapseDelayMs { get; set; } = 900;

    public bool HotkeyEnabled { get; set; } = true;

    public string Hotkey { get; set; } = DefaultHotkey;

    public bool ConfirmClear { get; set; }

    /// <summary>
    /// Clamps values that may have been edited by hand or written by a different version,
    /// so the rest of the app can trust them.
    /// </summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Edge))
            Edge = DockEdge.Right;
        if (double.IsNaN(VerticalPosition) || double.IsInfinity(VerticalPosition))
            VerticalPosition = 0.5;
        VerticalPosition = Math.Clamp(VerticalPosition, 0, 1);
        if (!CollapseDelayChoices.Contains(CollapseDelayMs))
            CollapseDelayMs = CollapseDelayChoices.MinBy(c => Math.Abs(c - CollapseDelayMs));
        if (string.IsNullOrWhiteSpace(Hotkey))
            Hotkey = DefaultHotkey;
        if (string.IsNullOrWhiteSpace(Display))
            Display = null;
        Version = CurrentVersion;
    }
}
