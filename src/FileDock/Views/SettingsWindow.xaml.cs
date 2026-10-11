using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FileDock.Native;
using FileDock.Services;
using FileDock.Themes;
using FileDock.ViewModels;

namespace FileDock.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
        ThemeManager.Changed += ApplyTitleBarTheme;
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.Changed -= ApplyTitleBarTheme;
        base.OnClosed(e);
    }

    /// <summary>Matches the title bar to the window body in dark mode (Windows 10 2004 and later).</summary>
    private void ApplyTitleBarTheme()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = ThemeManager.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---- Recording a shortcut. The current global shortcut is released while the box has focus,
    //      otherwise pressing it would toggle the shelf instead of reaching the box. ----

    private void OnShortcutFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm.BeginRecording();

    private void OnShortcutBlur(object sender, KeyboardFocusChangedEventArgs e) => _vm.EndRecording();

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            CloseButton.Focus(); // leaving the box cancels recording
            return;
        }
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = false; // keep normal keyboard navigation
            return;
        }
        if (Shortcut.IsModifierKey(key))
        {
            _vm.ShowPartial(Keyboard.Modifiers);
            return;
        }

        if (_vm.TryRecord(new Shortcut(Keyboard.Modifiers, key)))
            CloseButton.Focus();
    }

    private void OnShortcutKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        _vm.ShowPartial(Keyboard.Modifiers);
    }
}
