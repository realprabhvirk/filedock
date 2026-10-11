using System.Windows.Input;
using FileDock.Models;
using FileDock.Services;

namespace FileDock.ViewModels;

/// <summary>What the settings window needs from the app. Changes apply immediately.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }

    /// <summary>Called after any setting changes; the host re-applies placement and saves.</summary>
    void SettingsChanged();

    /// <summary>Re-registers the global shortcut from the settings. Returns an error message, or null.</summary>
    string? ApplyHotkey();

    /// <summary>Temporarily releases the global shortcut while a new one is being recorded.</summary>
    void SuspendHotkey();

    /// <summary>The error from the most recent shortcut registration, or null.</summary>
    string? HotkeyError { get; }
}

public sealed class DisplayOption(SettingsViewModel owner, string? deviceName, string label) : ObservableObject
{
    public string? DeviceName { get; } = deviceName;

    public string Label { get; } = label;

    public bool IsSelected
    {
        get => owner.IsDisplaySelected(DeviceName);
        set
        {
            if (value)
                owner.SelectDisplay(DeviceName);
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsHost _host;
    private string? _hotkeyMessage;
    private bool _hotkeyMessageIsError;
    private bool _isRecording;
    private string? _startupError;

    public SettingsViewModel(ISettingsHost host)
    {
        _host = host;
        ResetPositionCommand = new RelayCommand(() =>
        {
            S.VerticalPosition = 0.5;
            Changed(nameof(VerticalPosition));
        });
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);
        Displays = BuildDisplays();
        RefreshHotkeyStatus(_host.HotkeyError);
    }

    private AppSettings S => _host.Settings;

    public ICommand ResetPositionCommand { get; }

    public ICommand ResetDefaultsCommand { get; }

    // ---------- Position ----------

    public bool DockRight
    {
        get => S.Edge == DockEdge.Right;
        set
        {
            if (value && S.Edge != DockEdge.Right)
            {
                S.Edge = DockEdge.Right;
                Changed(nameof(DockRight), nameof(DockLeft));
            }
        }
    }

    public bool DockLeft
    {
        get => S.Edge == DockEdge.Left;
        set
        {
            if (value && S.Edge != DockEdge.Left)
            {
                S.Edge = DockEdge.Left;
                Changed(nameof(DockRight), nameof(DockLeft));
            }
        }
    }

    /// <summary>0 (top) to 100 (bottom).</summary>
    public double VerticalPosition
    {
        get => Math.Round(S.VerticalPosition * 100);
        set
        {
            double fraction = Math.Clamp(value / 100.0, 0, 1);
            if (Math.Abs(fraction - S.VerticalPosition) < 0.001)
                return;
            S.VerticalPosition = fraction;
            Changed(nameof(VerticalPosition));
        }
    }

    public IReadOnlyList<DisplayOption> Displays { get; private set; }

    public bool HasMultipleDisplays => Displays.Count > 1;

    internal bool IsDisplaySelected(string? device)
    {
        bool configuredExists = Displays.Any(d => d.DeviceName is not null && d.DeviceName == S.Display);
        return configuredExists ? device == S.Display : Displays.FirstOrDefault()?.DeviceName == device;
    }

    internal void SelectDisplay(string? device)
    {
        if (S.Display == device)
            return;
        S.Display = device;
        foreach (DisplayOption option in Displays)
            option.Refresh();
        Changed();
    }

    private List<DisplayOption> BuildDisplays()
    {
        // Primary first, then the others in the order Windows reports them.
        return WindowPlacementService.GetDisplays()
            .OrderByDescending(d => d.IsPrimary)
            .Select((d, i) => new DisplayOption(this, d.DeviceName,
                d.IsPrimary ? "Main display" : $"Display {i + 1}"))
            .ToList();
    }

    // ---------- Behaviour ----------

    public bool DelayShort
    {
        get => S.CollapseDelayMs == AppSettings.CollapseDelayChoices[0];
        set => SetDelay(value, AppSettings.CollapseDelayChoices[0]);
    }

    public bool DelayNormal
    {
        get => S.CollapseDelayMs == AppSettings.CollapseDelayChoices[1];
        set => SetDelay(value, AppSettings.CollapseDelayChoices[1]);
    }

    public bool DelayLong
    {
        get => S.CollapseDelayMs == AppSettings.CollapseDelayChoices[2];
        set => SetDelay(value, AppSettings.CollapseDelayChoices[2]);
    }

    private void SetDelay(bool selected, int ms)
    {
        if (!selected || S.CollapseDelayMs == ms)
            return;
        S.CollapseDelayMs = ms;
        Changed(nameof(DelayShort), nameof(DelayNormal), nameof(DelayLong));
    }

    public bool ConfirmClear
    {
        get => S.ConfirmClear;
        set
        {
            if (S.ConfirmClear == value)
                return;
            S.ConfirmClear = value;
            Changed(nameof(ConfirmClear));
        }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            StartupError = StartupRegistration.TrySet(value) ? null : "Windows did not allow this change.";
            OnPropertyChanged();
        }
    }

    public string? StartupError
    {
        get => _startupError;
        private set => SetProperty(ref _startupError, value);
    }

    // ---------- Shortcut ----------

    public bool HotkeyEnabled
    {
        get => S.HotkeyEnabled;
        set
        {
            if (S.HotkeyEnabled == value)
                return;
            S.HotkeyEnabled = value;
            OnPropertyChanged();
            RefreshHotkeyStatus(_host.ApplyHotkey());
            _host.SettingsChanged();
        }
    }

    public string HotkeyText => _isRecording
        ? (_partial.Length > 0 ? _partial + " + …" : "Press a shortcut…")
        : Shortcut.TryParse(S.Hotkey, out Shortcut? shortcut) ? shortcut.Value.ToString().Replace("+", " + ") : S.Hotkey;

    private string _partial = "";

    public string? HotkeyMessage
    {
        get => _hotkeyMessage;
        private set => SetProperty(ref _hotkeyMessage, value);
    }

    public bool HotkeyMessageIsError
    {
        get => _hotkeyMessageIsError;
        private set => SetProperty(ref _hotkeyMessageIsError, value);
    }

    public void BeginRecording()
    {
        _isRecording = true;
        _partial = "";
        _host.SuspendHotkey();
        OnPropertyChanged(nameof(HotkeyText));
        HotkeyMessageIsError = false;
        HotkeyMessage = "Use Ctrl, Alt or Win with another key. Press Esc to cancel.";
    }

    /// <summary>Shows the modifiers held so far while recording.</summary>
    public void ShowPartial(ModifierKeys modifiers)
    {
        if (!_isRecording)
            return;
        _partial = Shortcut.FormatModifiers(modifiers).Replace("+", " + ");
        OnPropertyChanged(nameof(HotkeyText));
    }

    /// <summary>Tries a recorded shortcut. Returns true when it was registered.</summary>
    public bool TryRecord(Shortcut shortcut)
    {
        if (!shortcut.IsValid)
        {
            HotkeyMessageIsError = true;
            HotkeyMessage = "Use Ctrl, Alt or Win together with another key.";
            return false;
        }

        string previous = S.Hotkey;
        S.Hotkey = shortcut.ToString();
        S.HotkeyEnabled = true;
        string? error = _host.ApplyHotkey();
        if (error is not null)
        {
            HotkeyMessageIsError = true;
            HotkeyMessage = error;
            S.Hotkey = previous;
            return false;
        }

        _isRecording = false;
        _host.SettingsChanged();
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HotkeyEnabled));
        RefreshHotkeyStatus(null);
        return true;
    }

    /// <summary>Leaves recording mode and restores the saved shortcut.</summary>
    public void EndRecording()
    {
        if (!_isRecording)
            return;
        _isRecording = false;
        OnPropertyChanged(nameof(HotkeyText));
        RefreshHotkeyStatus(_host.ApplyHotkey());
    }

    private void RefreshHotkeyStatus(string? error)
    {
        HotkeyMessageIsError = error is not null;
        HotkeyMessage = error ?? (S.HotkeyEnabled ? "Click the box to record a different shortcut." : null);
    }

    // ---------- Reset ----------

    private void ResetDefaults()
    {
        AppSettings defaults = new();
        S.Edge = defaults.Edge;
        S.VerticalPosition = defaults.VerticalPosition;
        S.Display = defaults.Display;
        S.CollapseDelayMs = defaults.CollapseDelayMs;
        S.ConfirmClear = defaults.ConfirmClear;
        S.HotkeyEnabled = defaults.HotkeyEnabled;
        S.Hotkey = defaults.Hotkey;
        if (StartupRegistration.IsEnabled())
            StartupRegistration.TrySet(false);

        foreach (DisplayOption option in Displays)
            option.Refresh();
        RefreshHotkeyStatus(_host.ApplyHotkey());
        Changed(nameof(DockRight), nameof(DockLeft), nameof(VerticalPosition), nameof(DelayShort), nameof(DelayNormal),
            nameof(DelayLong), nameof(ConfirmClear), nameof(StartWithWindows), nameof(HotkeyEnabled), nameof(HotkeyText));
    }

    private void Changed(params string[] properties)
    {
        foreach (string property in properties)
            OnPropertyChanged(property);
        _host.SettingsChanged();
    }
}
