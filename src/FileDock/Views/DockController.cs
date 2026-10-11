using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileDock.Models;
using FileDock.Native;
using FileDock.Services;
using FileDock.ViewModels;

namespace FileDock.Views;

public enum ExpandReason
{
    Hover,
    Drag,
    Explicit,
}

/// <summary>
/// Owns the collapsed-handle / expanded-panel behaviour of the shelf window.
/// <para>
/// The window keeps the expanded panel's size and position at all times. When collapsed, a
/// window region clips it to the small edge handle; expanding just removes the region. The HWND,
/// its size and its OLE drop-target registration therefore never change while a drag is in
/// progress, so an incoming drag that reaches the handle continues straight into the panel.
/// </para>
/// <para>
/// Auto-collapse is driven by a short watchdog timer that only runs while the panel is open.
/// It collapses the panel once the pointer has been outside it for the configured delay and
/// nothing else needs it open (pin, drag in either direction, menu or dialog, keyboard use).
/// </para>
/// </summary>
public sealed class DockController : IDisposable
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan IncomingDragGrace = TimeSpan.FromMilliseconds(400);

    private readonly Window _window;
    private readonly FrameworkElement _panel;
    private readonly FrameworkElement _handle;
    private readonly ShelfViewModel _vm;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _rejectedDragTimer;
    private readonly TranslateTransform _slide = new();

    private IntPtr _hwnd;
    private DockLayout? _layout;
    private DateTime _lastNeeded;
    private DateTime _lastIncomingDrag = DateTime.MinValue;
    private DateTime _holdUntil = DateTime.MinValue;
    private bool _explicitOpen;
    private bool _cursorInside;
    private int _outgoingDrags;
    private int _blockers;
    private int _animationGeneration;
    private uint _appBarMessage;
    private bool _appBarRegistered;
    private bool _fullscreenSuppressed;

    public DockController(Window window, FrameworkElement panel, FrameworkElement handle, ShelfViewModel vm, Func<AppSettings> settings)
    {
        _window = window;
        _panel = panel;
        _handle = handle;
        _vm = vm;
        _settings = settings;
        _panel.RenderTransform = _slide;

        _hoverTimer = new DispatcherTimer { Interval = HoverDelay };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (IsCursorOverHandle())
                Expand(ExpandReason.Hover);
        };

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _watchdog.Tick += (_, _) => CheckCollapse();

        _rejectedDragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _rejectedDragTimer.Tick += (_, _) =>
        {
            _rejectedDragTimer.Stop();
            if (!IsExpanded && DateTime.UtcNow - _lastIncomingDrag < TimeSpan.FromMilliseconds(250) && IsCursorOverHandle())
                Expand(ExpandReason.Drag);
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShelfViewModel.IsPinned))
                return;
            if (_vm.IsPinned)
                Expand(ExpandReason.Explicit);
            else
                _lastNeeded = DateTime.UtcNow;
        };
    }

    public bool IsExpanded { get; private set; }

    /// <summary>True when the user hid FileDock from the tray (the handle is not shown).</summary>
    public bool IsHidden { get; private set; }

    public bool IsOutgoingDrag => _outgoingDrags > 0;

    public DockEdge Edge => _settings().Edge;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        RegisterAppBar();
        Place();
    }

    /// <summary>Recomputes the layout for the configured display and edge and applies it.</summary>
    public void Place()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        AppSettings settings = _settings();
        DisplayInfo? display = WindowPlacementService.Resolve(settings.Display);
        if (display is null)
        {
            Log.Warn("No display found for placement");
            return;
        }

        _layout = WindowPlacementService.Compute(display.WorkArea, display.Scale, settings.Edge, settings.VerticalPosition);
        PixelRect w = _layout.Window;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, w.X, w.Y, w.Width, w.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

        double scale = _layout.Scale;
        PixelRect h = _layout.Handle;
        bool right = settings.Edge == DockEdge.Right;
        _handle.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _handle.VerticalAlignment = VerticalAlignment.Top;
        _handle.Width = h.Width / scale;
        _handle.Height = h.Height / scale;
        _handle.Margin = new Thickness(0, h.Y / scale, 0, 0);
        if (_handle is System.Windows.Controls.Border handleBorder)
            handleBorder.BorderThickness = right ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);
        if (_panel is System.Windows.Controls.Border panelBorder)
            panelBorder.BorderThickness = right ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);

        ApplyRegion();
    }

    // ---------- Expand / collapse ----------

    public void Expand(ExpandReason reason, bool activate = false)
    {
        if (IsHidden)
            return;
        _hoverTimer.Stop();
        if (reason == ExpandReason.Explicit)
            _explicitOpen = true;
        _lastNeeded = DateTime.UtcNow;

        if (!IsExpanded)
        {
            IsExpanded = true;
            _animationGeneration++; // cancels a collapse that is still fading out
            bool stillFading = _panel.Visibility == Visibility.Visible;
            double startOpacity = stillFading ? _panel.Opacity : 0;
            double startOffset = stillFading ? _slide.X : Edge == DockEdge.Right ? 14 : -14;
            _handle.Visibility = Visibility.Collapsed;
            _panel.Visibility = Visibility.Visible;
            ApplyRegion();
            Animate(fromOpacity: startOpacity, toOpacity: 1, fromOffset: startOffset, durationMs: 130, onDone: null);
            _watchdog.Start();
            _vm.RefreshAvailability();
            Log.Info($"Expanded ({reason})");
        }

        if (activate)
        {
            _window.Activate();
            Win32.SetForegroundWindow(_hwnd);
        }
    }

    public void Collapse(bool immediate = false)
    {
        if (!IsExpanded)
            return;
        IsExpanded = false;
        _explicitOpen = false;
        _holdUntil = DateTime.MinValue;
        _watchdog.Stop();
        _vm.HideDropOverlay();
        Log.Info("Collapsed");
        SetCursorInside(false);
        int generation = ++_animationGeneration;

        void Finish()
        {
            if (generation != _animationGeneration || IsExpanded)
                return;
            _panel.Visibility = Visibility.Hidden;
            _handle.Visibility = Visibility.Visible;
            ApplyRegion();
        }

        if (immediate || !SystemParameters.ClientAreaAnimation)
            Finish();
        else
            Animate(fromOpacity: 1, toOpacity: 0, fromOffset: 0, durationMs: 90, onDone: Finish,
                toOffset: Edge == DockEdge.Right ? 10 : -10);
    }

    /// <summary>Global shortcut behaviour: open and focus the panel, or close it if it is open.</summary>
    public void Toggle()
    {
        if (IsHidden || !IsExpanded)
        {
            Show(activate: true);
        }
        else
        {
            _vm.IsPinned = false;
            Collapse();
        }
    }

    /// <summary>Shows the panel explicitly (tray, shortcut, second launch).</summary>
    public void Show(bool activate)
    {
        if (IsHidden)
        {
            IsHidden = false;
            _window.Show();
            Place();
        }
        Expand(ExpandReason.Explicit, activate);
    }

    /// <summary>Hides FileDock completely, including the handle, until shown again.</summary>
    public void Hide()
    {
        _vm.IsPinned = false;
        Collapse(immediate: true);
        IsHidden = true;
        _window.Hide();
    }

    // ---------- Inputs that keep the panel open ----------

    public void OnHandleMouseEnter() => _hoverTimer.Start();

    public void OnHandleMouseLeave() => _hoverTimer.Stop();

    /// <summary>Called for every incoming drag event that carries files. Expands immediately.</summary>
    public void NoteIncomingDrag()
    {
        _lastIncomingDrag = DateTime.UtcNow;
        if (!IsExpanded)
            Expand(ExpandReason.Drag);
    }

    /// <summary>
    /// Called for drag events that cannot be dropped (text, links, items inside archives). The
    /// panel opens only if the drag rests on the handle briefly, to explain why, so that a drag
    /// merely passing the screen edge does not pop it open.
    /// </summary>
    public void NoteRejectedDrag()
    {
        _lastIncomingDrag = DateTime.UtcNow;
        if (!IsExpanded && !_rejectedDragTimer.IsEnabled)
            _rejectedDragTimer.Start();
    }

    public void EndIncomingDrag()
    {
        _rejectedDragTimer.Stop();
        _lastIncomingDrag = DateTime.MinValue;
        _lastNeeded = DateTime.UtcNow;
    }

    public IDisposable BeginOutgoingDrag()
    {
        _outgoingDrags++;
        return new Scope(() =>
        {
            _outgoingDrags--;
            _lastNeeded = DateTime.UtcNow;
        });
    }

    /// <summary>Keeps the panel open for a while so an important message after a drag can be read.</summary>
    public void HoldOpen(TimeSpan duration)
    {
        _holdUntil = DateTime.UtcNow + duration;
        if (!IsExpanded)
            Expand(ExpandReason.Explicit);
    }

    /// <summary>Keeps the panel open while a menu or dialog is showing.</summary>
    public IDisposable Block()
    {
        _blockers++;
        return new Scope(() =>
        {
            _blockers--;
            _lastNeeded = DateTime.UtcNow;
        });
    }

    private void CheckCollapse()
    {
        if (!IsExpanded)
        {
            _watchdog.Stop();
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool inside = IsCursorInsideWindow();
        SetCursorInside(inside);

        bool needed = inside
                      || _vm.IsPinned
                      || _outgoingDrags > 0
                      || _blockers > 0
                      || now - _lastIncomingDrag < IncomingDragGrace
                      || now < _holdUntil
                      || (_explicitOpen && _window.IsActive);
        if (needed)
        {
            _lastNeeded = now;
            return;
        }

        if (now - _lastNeeded >= TimeSpan.FromMilliseconds(_settings().CollapseDelayMs))
            Collapse();
    }

    private void SetCursorInside(bool inside)
    {
        if (inside == _cursorInside)
            return;
        _cursorInside = inside;
        // Keep "Removed · Undo" and similar notices on screen while the user is looking at them.
        _vm.HoldNotice(inside);
    }

    private bool IsCursorInsideWindow()
    {
        if (!Win32.GetCursorPos(out Win32.POINT p) || !Win32.GetWindowRect(_hwnd, out Win32.RECT r))
            return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    private bool IsCursorOverHandle()
    {
        if (_layout is null || !Win32.GetCursorPos(out Win32.POINT p) || !Win32.GetWindowRect(_hwnd, out Win32.RECT r))
            return false;
        return _layout.Handle.Contains(p.X - r.Left, p.Y - r.Top);
    }

    // ---------- Region and animation ----------

    private void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero || _layout is null)
            return;

        IntPtr region;
        if (IsExpanded || _panel.Visibility == Visibility.Visible)
            region = IntPtr.Zero; // whole window
        else if (_fullscreenSuppressed)
            region = Win32.CreateRectRgn(0, 0, 0, 0); // nothing visible over a full-screen app
        else
        {
            PixelRect h = _layout.Handle;
            region = Win32.CreateRectRgn(h.X, h.Y, h.Right, h.Bottom);
        }

        // On success the system owns the region; on failure it must be freed here.
        if (Win32.SetWindowRgn(_hwnd, region, true) == 0 && region != IntPtr.Zero)
            Win32.DeleteObject(region);
    }

    private void Animate(double fromOpacity, double toOpacity, double fromOffset, int durationMs, Action? onDone, double toOffset = 0)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _panel.BeginAnimation(UIElement.OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _panel.Opacity = toOpacity;
            _slide.X = toOffset;
            onDone?.Invoke();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        if (onDone is not null)
            fade.Completed += (_, _) => onDone();
        _panel.BeginAnimation(UIElement.OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromOffset, toOffset, duration) { EasingFunction = ease });
    }

    // ---------- Window messages: display changes and full-screen apps ----------

    public void HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
            case Win32.WM_SETTINGCHANGE when wParam.ToInt32() == Win32.SPI_SETWORKAREA:
                // Let WPF finish its own handling (it applies the suggested DPI rectangle) first.
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Place);
                break;
        }

        if (_appBarRegistered && msg == (int)_appBarMessage && wParam.ToInt32() == Win32.ABN_FULLSCREENAPP)
        {
            bool suppress = lParam != IntPtr.Zero && ForegroundCoversOurDisplay();
            if (suppress != _fullscreenSuppressed)
            {
                _fullscreenSuppressed = suppress;
                Log.Info(suppress ? "Full-screen app detected; hiding handle" : "Full-screen app closed; showing handle");
                ApplyRegion();
            }
        }
    }

    /// <summary>
    /// Registers as an app bar purely to receive ABN_FULLSCREENAPP, so the handle can step aside
    /// for full-screen video, games and presentations. No screen space is reserved.
    /// </summary>
    private void RegisterAppBar()
    {
        _appBarMessage = Win32.RegisterWindowMessage("FileDock.AppBarNotify");
        var data = new Win32.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _appBarMessage,
        };
        _appBarRegistered = Win32.SHAppBarMessage(Win32.ABM_NEW, ref data) != UIntPtr.Zero;
    }

    private bool ForegroundCoversOurDisplay()
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd
            || foreground == Win32.GetShellWindow() || foreground == Win32.GetDesktopWindow())
            return false;

        var className = new StringBuilder(64);
        Win32.GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd")
            return false;

        DisplayInfo? display = WindowPlacementService.Resolve(_settings().Display);
        if (display is null || !Win32.GetWindowRect(foreground, out Win32.RECT r))
            return false;
        PixelRect b = display.Bounds;
        return r.Left <= b.X && r.Top <= b.Y && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    public void Dispose()
    {
        _hoverTimer.Stop();
        _watchdog.Stop();
        _rejectedDragTimer.Stop();
        if (_appBarRegistered)
        {
            var data = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = _hwnd };
            Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref data);
            _appBarRegistered = false;
        }
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }
}
