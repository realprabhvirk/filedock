using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FileDock.Models;
using FileDock.Services;
using FileDock.Themes;
using FileDock.ViewModels;
using FileDock.Views;

namespace FileDock;

/// <summary>
/// Composition root and lifecycle: single instance, startup, the tray, settings and a clean exit.
/// </summary>
public partial class App : Application, ISettingsHost
{
    private const string MutexName = @"Local\FileDock.SingleInstance";
    private const string ShowEventName = @"Local\FileDock.ShowRequest";

    private readonly HotkeyService _hotkey = new();
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private SettingsService _settings = null!;
    private ShelfService _shelf = null!;
    private IconService? _icons;
    private ShelfViewModel _vm = null!;
    private ShelfWindow? _window;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _saveSettingsTimer;
    private string? _hotkeyError;
    private bool _exiting;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileDock");

    public AppSettings Settings => _settings.Current;

    public string? HotkeyError => _hotkeyError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize(DataDirectory);

        // One FileDock per user session. A second launch asks the first one to show itself.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                Log.Warn("Another instance is running but could not be signalled", ex);
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception (terminating)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Log.Info($"FileDock {version} starting (data: {DataDirectory})");

        try
        {
            Start();
        }
        catch (Exception ex)
        {
            // Without its window FileDock is useless; say so plainly instead of lingering invisibly.
            Log.Error("Startup failed", ex);
            MessageBox.Show($"FileDock could not start.\n\n{ex.Message}\n\nDetails were written to:\n{Path.Combine(DataDirectory, "filedock.log")}",
                "FileDock", MessageBoxButton.OK, MessageBoxImage.Error);
            _tray?.Dispose();
            Shutdown(1);
        }
    }

    private void Start()
    {
        ThemeManager.Start(Resources);
        ThemeManager.Changed += OnThemeChanged;

        _settings = new SettingsService(Path.Combine(DataDirectory, "settings.json"));
        _settings.Load();
        _saveSettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveSettingsTimer.Tick += (_, _) =>
        {
            _saveSettingsTimer.Stop();
            _settings.Save();
        };

        _shelf = new ShelfService(Path.Combine(DataDirectory, "shelf.json"));
        _icons = new IconService();
        _vm = new ShelfViewModel(_shelf, _icons, () => _settings.Current);
        _vm.SettingsRequested += OpenSettings;
        LoadResult<ShelfState> shelfLoad = _shelf.Load();
        Log.Info($"Loaded {_shelf.Count} shelf item(s) ({shelfLoad.Status})");

        _window = new ShelfWindow(_vm, () => _settings.Current, _hotkey);
        _hotkey.Pressed += OnHotkey;
        _window.Show();
        string? hotkeyNotice = ApplyHotkeyAtStartup();

        _tray = CreateTray();
        _vm.Items.CollectionChanged += (_, _) => _tray?.UpdateTooltip(_vm.Items.Count);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowShelf), null, Timeout.Infinite, executeOnlyOnce: false);

        ReportStartupProblems(shelfLoad, hotkeyNotice);
        _vm.RefreshAvailability(force: true);
        Log.Info($"Started in {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms");
    }

    /// <summary>
    /// Registers the shortcut. If the untouched default is owned by another app, switches once to
    /// <see cref="AppSettings.FallbackHotkey"/> and returns a message explaining the change.
    /// </summary>
    private string? ApplyHotkeyAtStartup()
    {
        if (ApplyHotkey() is null)
            return null;

        AppSettings settings = _settings.Current;
        if (!settings.HotkeyEnabled || settings.Hotkey != AppSettings.DefaultHotkey)
            return null;

        settings.Hotkey = AppSettings.FallbackHotkey;
        if (ApplyHotkey() is null)
        {
            _settings.Save();
            Log.Info($"Default shortcut unavailable; using {AppSettings.FallbackHotkey}");
            // Word joiners keep each shortcut on one line when the notice wraps.
            static string NoBreak(string s) => s.Replace("+", "+⁠");
            return $"{NoBreak(AppSettings.DefaultHotkey)} is used by another app, so FileDock's shortcut is {NoBreak(AppSettings.FallbackHotkey)}.";
        }

        settings.Hotkey = AppSettings.DefaultHotkey;
        ApplyHotkey();
        return null;
    }

    private TrayService CreateTray()
    {
        Stream iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/FileDock.ico")).Stream;
        var tray = new TrayService(new TrayCommands
        {
            Show = ShowShelf,
            Hide = () => _window?.Dock.Hide(),
            TogglePin = () =>
            {
                if (_window?.Dock.IsHidden == true)
                    _window.Dock.Show(activate: false);
                _vm.IsPinned = !_vm.IsPinned;
            },
            Clear = () =>
            {
                // Show the shelf so the "Undo" that follows is visible.
                ShowShelf();
                _vm.Clear();
            },
            OpenSettings = OpenSettings,
            Exit = ExitApplication,
            IsVisible = () => _window?.Dock.IsHidden == false,
            IsPinned = () => _vm.IsPinned,
            ItemCount = () => _vm.Items.Count,
        }, iconStream);
        tray.ApplyTheme(ThemeManager.IsDark);
        tray.UpdateTooltip(_vm.Items.Count);
        Log.Info($"Tray ready ({(ThemeManager.IsDark ? "dark" : "light")} theme)");
        return tray;
    }

    private void ShowShelf()
    {
        if (_window is null)
            return;
        _window.Dock.Show(activate: true);
        _window.FocusList();
    }

    private void OnHotkey()
    {
        if (_window is null)
            return;
        _window.Dock.Toggle();
        if (_window.Dock.IsExpanded)
            _window.FocusList();
    }

    private void ReportStartupProblems(LoadResult<ShelfState> shelfLoad, string? hotkeyNotice)
    {
        if (shelfLoad.Status == LoadStatus.Corrupt)
        {
            _vm.ShowNotice("The saved shelf was damaged, so FileDock started empty. A copy was kept in the FileDock data folder.",
                NoticeKind.Error, sticky: true);
        }
        else if (shelfLoad.Status == LoadStatus.Unreadable)
        {
            _vm.ShowNotice("The saved shelf could not be opened. Changes made now will not be saved.",
                NoticeKind.Error, sticky: true);
        }
        else if (_hotkeyError is not null)
        {
            _vm.ShowNotice(_hotkeyError, NoticeKind.Error, "Settings", OpenSettings, sticky: true);
        }
        else if (hotkeyNotice is not null)
        {
            _vm.ShowNotice(hotkeyNotice, NoticeKind.Info, "Change", OpenSettings, sticky: true);
        }
        else if (_settings.LoadStatus == LoadStatus.Corrupt)
        {
            _vm.ShowNotice("Settings were reset because the settings file was damaged.", sticky: true);
        }
    }

    // ---------- ISettingsHost ----------

    public void SettingsChanged()
    {
        _window?.Dock.Place();
        _saveSettingsTimer?.Stop();
        _saveSettingsTimer?.Start();
    }

    public string? ApplyHotkey()
    {
        _hotkey.Unregister();
        _hotkeyError = null;
        AppSettings settings = _settings.Current;
        if (!settings.HotkeyEnabled)
            return null;
        if (!Shortcut.TryParse(settings.Hotkey, out Shortcut? shortcut))
            _hotkeyError = $"“{settings.Hotkey}” is not a usable shortcut. Choose another in Settings.";
        else if (!_hotkey.TryRegister(shortcut.Value, out string? error))
            _hotkeyError = error;
        return _hotkeyError;
    }

    public void SuspendHotkey() => _hotkey.Unregister();

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(new SettingsViewModel(this));
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _saveSettingsTimer?.Stop();
            _settings.Save();
            if (_hotkey.Active is null && _settings.Current.HotkeyEnabled)
                ApplyHotkey();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnThemeChanged()
    {
        _tray?.ApplyTheme(ThemeManager.IsDark);
        _vm.ReloadIcons();
    }

    // ---------- Errors and shutdown ----------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        _vm?.ShowNotice("Something went wrong. FileDock is still running; details were written to its log.", NoticeKind.Error);
    }

    private void ExitApplication()
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("Exiting");

        _saveSettingsTimer?.Stop();
        _settings.Save();
        _shelf.Save();

        _settingsWindow?.Close();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _hotkey.Dispose();
        _tray?.Dispose();
        _window?.CloseForExit();
        _icons?.Dispose();
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Stop();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is signing out or shutting down: make sure everything is on disk.
        _settings?.Save();
        _shelf?.Save();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; disposing is enough.
            }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
