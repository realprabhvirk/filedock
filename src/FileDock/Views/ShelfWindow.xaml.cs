using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FileDock.Models;
using FileDock.Native;
using FileDock.Services;
using FileDock.ViewModels;

namespace FileDock.Views;

public partial class ShelfWindow : Window
{
    private readonly ShelfViewModel _vm;
    private readonly HotkeyService _hotkey;
    private readonly DropImageHelper _dropImage = new();
    private IntPtr _hwnd;
    private bool _allowClose;

    // Incoming drag session. WPF raises DragLeave/DragEnter pairs as the pointer crosses child
    // elements, so a session only ends when no drag event follows a DragLeave.
    private IDataObject? _dragData;
    private string[]? _dragPaths;
    private int _dragEventCount;

    // Outgoing drag gesture.
    private Point? _pressPoint;
    private ItemViewModel? _pressItem;
    private ItemViewModel? _deferredSelection;
    private IDisposable? _menuBlock;

    public ShelfWindow(ShelfViewModel vm, Func<AppSettings> settings, HotkeyService hotkey)
    {
        InitializeComponent();
        _vm = vm;
        _hotkey = hotkey;
        DataContext = vm;
        Dock = new DockController(this, PanelRoot, HandleView, vm, settings);

        _vm.ItemsAdded += items => ItemsList.ScrollIntoView(items[^1]);
        _vm.ConfirmClear = message =>
        {
            using (Dock.Block())
                return MessageBox.Show(this, message, "FileDock", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
        };

        PreviewDragEnter += (_, e) => OnDragEvent(e);
        PreviewDragOver += (_, e) => OnDragEvent(e);
        PreviewDragLeave += OnDragLeave;
        PreviewDrop += OnDrop;
        PreviewKeyDown += OnKeyDown;
        DpiChanged += (_, e) => UpdateIconSize(e.NewDpi.DpiScaleX);
    }

    public DockController Dock { get; }

    public IntPtr Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: no taskbar button and no Alt+Tab entry.
        long style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        style = (style | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(style));

        // Windows 11 would otherwise round the corners of the strip at the screen edge.
        int corner = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _hotkey.Attach(_hwnd);
        Dock.Attach(_hwnd);
        UpdateIconSize(VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkey.HandleMessage(msg, wParam))
        {
            handled = true;
            return IntPtr.Zero;
        }
        Dock.HandleMessage(msg, wParam, lParam);
        return IntPtr.Zero;
    }

    private void UpdateIconSize(double scale)
    {
        int size = (int)Math.Round(20 * scale);
        if (size == _vm.IconPixelSize)
            return;
        _vm.IconPixelSize = size;
        _vm.ReloadIcons();
    }

    /// <summary>Closes the window for real (application exit). Other close attempts just collapse it.</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Dock.Dispose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _vm.IsPinned = false;
            Dock.Collapse();
        }
        base.OnClosing(e);
    }

    // ================= Incoming drag and drop =================

    private void OnDragEvent(DragEventArgs e)
    {
        e.Handled = true;
        _dragEventCount++;

        if (Dock.IsOutgoingDrag)
        {
            // Our own outgoing drag passing back over the shelf: nothing to add.
            e.Effects = DragDropEffects.None;
            return;
        }

        bool newSession = !ReferenceEquals(e.Data, _dragData);
        if (newSession)
        {
            _dragData = e.Data;
            _dragPaths = DragDropService.TryGetFileDrop(e.Data);
        }

        // Never report Move: a source such as Explorer may delete the originals after an
        // "unoptimised move". Copy is the honest effect for "add a reference to the shelf".
        DragDropEffects effect = DragDropEffects.None;
        if (_dragPaths is { Length: > 0 })
        {
            if (e.AllowedEffects.HasFlag(DragDropEffects.Copy))
                effect = DragDropEffects.Copy;
            else if (e.AllowedEffects.HasFlag(DragDropEffects.Link))
                effect = DragDropEffects.Link;
        }
        e.Effects = effect;

        if (effect != DragDropEffects.None)
            Dock.NoteIncomingDrag(); // expands the panel immediately if it is collapsed
        else
            Dock.NoteRejectedDrag(); // expands after a short rest, to explain the refusal

        Win32.GetCursorPos(out Win32.POINT point);
        if (newSession)
        {
            if (Dock.IsExpanded)
                _vm.ShowDropOverlay(_dragPaths);
            if (effect != DragDropEffects.None)
                DragDropService.SetDropDescription(e.Data, ShellInterop.DROPIMAGE_COPY, "Add to %1", "FileDock");
            _dropImage.DragEnter(_hwnd, e.Data, point, effect);
        }
        else
        {
            if (Dock.IsExpanded && !_vm.IsDropOverlayVisible)
                _vm.ShowDropOverlay(_dragPaths);
            _dropImage.DragOver(point, effect);
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int countAtLeave = _dragEventCount;
        // If another drag event arrives before this runs, the pointer only moved between child elements.
        Dispatcher.BeginInvoke(() =>
        {
            if (countAtLeave == _dragEventCount && _dragData is not null)
            {
                Log.Info("Incoming drag left without a drop");
                EndDragSession(dropped: false);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragEventCount++;
        if (Dock.IsOutgoingDrag)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        string[]? paths = ReferenceEquals(e.Data, _dragData) ? _dragPaths : DragDropService.TryGetFileDrop(e.Data);
        e.Effects = paths is { Length: > 0 }
            ? e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link
            : DragDropEffects.None;

        Win32.GetCursorPos(out Win32.POINT point);
        _dropImage.Drop(e.Data, point, e.Effects);
        EndDragSession(dropped: true);
        Log.Info($"Drop received: {paths?.Length ?? 0} path(s), effect {e.Effects}");

        if (paths is not { Length: > 0 })
        {
            _vm.ShowNotice("Only files and folders can be added to the shelf.", NoticeKind.Error);
            return;
        }

        try
        {
            await _vm.AddDroppedAsync(paths);
        }
        catch (Exception ex)
        {
            Log.Error("Adding dropped items failed", ex);
            _vm.ShowNotice("Something went wrong while adding those items.", NoticeKind.Error);
        }
    }

    private void EndDragSession(bool dropped)
    {
        if (!dropped && _dragData is not null)
        {
            // Hand the drag image caption back to the source's default.
            DragDropService.SetDropDescription(_dragData, ShellInterop.DROPIMAGE_INVALID, "", "");
            _dropImage.DragLeave();
        }
        _dragData = null;
        _dragPaths = null;
        _vm.HideDropOverlay();
        Dock.EndIncomingDrag();
    }

    // ================= Outgoing drag =================

    private void OnDragAllMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        DragAllStrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragAllMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is null || e.LeftButton != MouseButtonState.Pressed || !ExceedsDragThreshold(e.GetPosition(this)))
            return;
        _pressPoint = null;
        DragAllStrip.ReleaseMouseCapture();
        StartDragOut(_vm.Items.ToList());
    }

    private void OnDragAllMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = null;
        DragAllStrip.ReleaseMouseCapture();
    }

    private void OnDragAllLostCapture(object sender, MouseEventArgs e) => _pressPoint = null;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressItem = null;
        _deferredSelection = null;
        if (e.OriginalSource is not DependencyObject source || FindAncestor<ButtonBase>(source) is not null)
            return;
        if (FindAncestor<ListBoxItem>(source) is not { DataContext: ItemViewModel item } container)
            return;

        _pressPoint = e.GetPosition(this);
        _pressItem = item;

        // Pressing on an already-selected row must not collapse a multi-selection, or the user
        // could never drag several items. The selection is reduced on mouse-up if no drag started.
        if (container.IsSelected && Keyboard.Modifiers == ModifierKeys.None && ItemsList.SelectedItems.Count > 1)
        {
            _deferredSelection = item;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressItem is null || e.LeftButton != MouseButtonState.Pressed || !ExceedsDragThreshold(e.GetPosition(this)))
            return;

        ItemViewModel pressed = _pressItem;
        _pressItem = null;
        _deferredSelection = null;
        _pressPoint = null;

        var selected = ItemsList.SelectedItems.Cast<ItemViewModel>().ToList();
        IReadOnlyList<ItemViewModel> items = DragSelection.Resolve(_vm.Items, selected, pressed);
        if (!selected.Contains(pressed))
            ItemsList.SelectedItem = pressed;

        Mouse.Capture(null);
        StartDragOut(items);
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelection is not null)
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = _deferredSelection;
        }
        _deferredSelection = null;
        _pressItem = null;
        _pressPoint = null;
    }

    private bool ExceedsDragThreshold(Point current)
    {
        if (_pressPoint is not Point start)
            return false;
        return Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private void StartDragOut(IReadOnlyList<ItemViewModel> items)
    {
        if (items.Count == 0)
            return;

        var available = items.Where(i => i.IsAvailable).ToList();
        int skipped = items.Count - available.Count;
        if (available.Count == 0)
        {
            _vm.ShowNotice(items.Count == 1
                ? "This item is unavailable. It may have been moved or deleted."
                : "These items are unavailable. They may have been moved or deleted.", NoticeKind.Error,
                "Remove", () => _vm.RemoveUnavailableCommand.Execute(null));
            return;
        }

        DragOutResult result;
        using (Dock.BeginOutgoingDrag())
            result = DragDropService.DragOut(_hwnd, this, available.Select(i => i.Path).ToList());

        if (result.Unavailable.Count > 0)
            _vm.MarkUnavailable(result.Unavailable);

        int leftOut = skipped + result.Unavailable.Count;
        if (leftOut > 0)
        {
            _vm.ShowNotice(
                $"{ShelfViewModel.Plural(leftOut, "unavailable item")} {(leftOut == 1 ? "was" : "were")} left out of the drag.",
                NoticeKind.Error, "Remove", () => _vm.RemoveUnavailableCommand.Execute(null));
            // The pointer is usually over the drop target by now; keep the message visible.
            Dock.HoldOpen(TimeSpan.FromSeconds(5));
        }
    }

    // ================= Selection, keyboard, menus =================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedCount = ItemsList.SelectedItems.Count;
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!Dock.IsExpanded)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                _vm.IsPinned = false;
                Dock.Collapse();
                e.Handled = true;
                break;
            case Key.Delete when ItemsList.SelectedItems.Count > 0:
                _vm.RemoveSelectedCommand.Execute(ItemsList.SelectedItems);
                e.Handled = true;
                break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                ItemsList.SelectAll();
                ItemsList.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves keyboard focus into the list (after the panel is opened with the shortcut).</summary>
    public void FocusList()
    {
        if (ItemsList.Items.Count == 0)
        {
            ItemsList.Focus();
            return;
        }
        int index = Math.Max(0, ItemsList.SelectedIndex);
        if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
            container.Focus();
        else
            ItemsList.Focus();
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-clicking empty space has nothing to act on.
        if (ItemsList.SelectedItems.Count == 0)
            e.Handled = true;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuBlock?.Dispose();
        _menuBlock = Dock.Block();
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuBlock?.Dispose();
        _menuBlock = null;
    }

    private void OnHandleMouseEnter(object sender, MouseEventArgs e) => Dock.OnHandleMouseEnter();

    private void OnHandleMouseLeave(object sender, MouseEventArgs e) => Dock.OnHandleMouseLeave();

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
