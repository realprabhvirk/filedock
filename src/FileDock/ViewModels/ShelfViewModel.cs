using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FileDock.Models;
using FileDock.Native;
using FileDock.Services;

namespace FileDock.ViewModels;

public enum NoticeKind
{
    Info,
    Error,
}

/// <summary>State and commands for the shelf panel.</summary>
public sealed class ShelfViewModel : ObservableObject
{
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);

    private readonly ShelfService _shelf;
    private readonly IconService _icons;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ItemViewModel> _byPath = new(FileSystemService.PathComparer);
    private readonly DispatcherTimer _noticeTimer;
    private IReadOnlyList<RemovedEntry>? _undo;
    private int _refreshGeneration;
    private DateTime _lastRefresh = DateTime.MinValue;

    private bool _isPinned;
    private int _selectedCount;
    private int _unavailableCount;
    private string? _noticeText;
    private NoticeKind _noticeKind;
    private string? _noticeActionText;
    private Action? _noticeAction;
    private bool _noticeIsSticky;
    private bool _isDropOverlayVisible;
    private bool _dropAllowed;
    private string _dropTitle = "";
    private string _dropDetail = "";

    public ShelfViewModel(ShelfService shelf, IconService icons, Func<AppSettings> settings)
    {
        _shelf = shelf;
        _icons = icons;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _noticeTimer = new DispatcherTimer { Interval = InfoDuration };
        _noticeTimer.Tick += (_, _) => DismissNotice();

        Items.CollectionChanged += (_, _) => OnItemsChanged();
        _shelf.Changed += (_, _) => Sync();
        _shelf.SaveFailed += ex => _dispatcher.BeginInvoke(() =>
            ShowNotice("Changes could not be saved. They will be kept until FileDock closes.", NoticeKind.Error, sticky: true));

        RemoveSelectedCommand = new RelayCommand(p => Remove(AsItems(p)), p => AsItems(p).Count > 0);
        RemoveItemCommand = new RelayCommand(p => { if (p is ItemViewModel item) Remove([item]); });
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0);
        RemoveUnavailableCommand = new RelayCommand(() => Remove(Items.Where(i => !i.IsAvailable).ToList()));
        OpenCommand = new RelayCommand(p => Open(AsItems(p)), p => AsItems(p) is [{ IsAvailable: true }]);
        RevealCommand = new RelayCommand(p => Reveal(AsItems(p)), p => AsItems(p).Any(i => i.IsAvailable));
        CopyPathCommand = new RelayCommand(p => CopyPaths(AsItems(p)), p => AsItems(p).Count > 0);
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        DismissNoticeCommand = new RelayCommand(DismissNotice);
        NoticeActionCommand = new RelayCommand(() =>
        {
            Action? action = _noticeAction;
            DismissNotice();
            action?.Invoke();
        });
    }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    /// <summary>Icon size in physical pixels for the current display scale.</summary>
    public int IconPixelSize { get; set; } = 20;

    /// <summary>Asks the user to confirm clearing the shelf; supplied by the view.</summary>
    public Func<string, bool>? ConfirmClear { get; set; }

    public event Action? SettingsRequested;

    /// <summary>Raised after a drop added items, so the view can scroll to them.</summary>
    public event Action<IReadOnlyList<ItemViewModel>>? ItemsAdded;

    public ICommand RemoveSelectedCommand { get; }
    public ICommand RemoveItemCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RemoveUnavailableCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand NoticeActionCommand { get; }

    // ---------- Header and footer text ----------

    public bool IsEmpty => Items.Count == 0;

    public string CountText => Plural(Items.Count, "item");

    public string DragAllText => Items.Count == 1 ? "Drag 1 item" : $"Drag all {Items.Count} items";

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        set
        {
            if (SetProperty(ref _selectedCount, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public int UnavailableCount
    {
        get => _unavailableCount;
        private set
        {
            if (SetProperty(ref _unavailableCount, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => SelectedCount > 0 ? $"{SelectedCount} selected"
        : UnavailableCount > 0 ? $"{UnavailableCount} unavailable"
        : "";

    // ---------- Notice line ----------

    public string? NoticeText
    {
        get => _noticeText;
        private set
        {
            if (SetProperty(ref _noticeText, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _noticeText is not null;

    public NoticeKind NoticeKind
    {
        get => _noticeKind;
        private set => SetProperty(ref _noticeKind, value);
    }

    public string? NoticeActionText
    {
        get => _noticeActionText;
        private set => SetProperty(ref _noticeActionText, value);
    }

    public bool NoticeIsSticky
    {
        get => _noticeIsSticky;
        private set => SetProperty(ref _noticeIsSticky, value);
    }

    public void ShowNotice(string text, NoticeKind kind = NoticeKind.Info, string? actionText = null,
        Action? action = null, TimeSpan? duration = null, bool sticky = false)
    {
        _noticeTimer.Stop();
        NoticeKind = kind;
        NoticeActionText = actionText;
        _noticeAction = action;
        NoticeIsSticky = sticky;
        NoticeText = text;
        if (!sticky)
        {
            _noticeTimer.Interval = duration ?? (kind == NoticeKind.Error ? ErrorDuration : InfoDuration);
            _noticeTimer.Start();
        }
    }

    public void DismissNotice()
    {
        _noticeTimer.Stop();
        NoticeText = null;
        NoticeActionText = null;
        _noticeAction = null;
        NoticeIsSticky = false;
    }

    /// <summary>Keeps a transient notice on screen while the user is looking at the panel.</summary>
    public void HoldNotice(bool hold)
    {
        if (hold)
            _noticeTimer.Stop();
        else if (HasNotice && !NoticeIsSticky)
            _noticeTimer.Start();
    }

    // ---------- Drop overlay ----------

    public bool IsDropOverlayVisible
    {
        get => _isDropOverlayVisible;
        private set => SetProperty(ref _isDropOverlayVisible, value);
    }

    public bool DropAllowed
    {
        get => _dropAllowed;
        private set => SetProperty(ref _dropAllowed, value);
    }

    public string DropTitle
    {
        get => _dropTitle;
        private set => SetProperty(ref _dropTitle, value);
    }

    public string DropDetail
    {
        get => _dropDetail;
        private set => SetProperty(ref _dropDetail, value);
    }

    /// <summary>Describes an incoming drag. <paramref name="paths"/> is null when the data is not a file drop.</summary>
    public void ShowDropOverlay(string[]? paths)
    {
        if (paths is null)
        {
            DropAllowed = false;
            DropTitle = "Only files and folders can be added";
            DropDetail = "Save text, links or images as files first. For items inside a .zip, extract them first.";
        }
        else
        {
            int fresh = paths.Count(p => !_shelf.Contains(p));
            DropAllowed = true;
            DropTitle = fresh == 0 ? "Already on the shelf" : $"Drop to add {Plural(fresh, "item")}";
            DropDetail = "Files stay where they are. FileDock only remembers them.";
        }
        IsDropOverlayVisible = true;
    }

    public void HideDropOverlay() => IsDropOverlayVisible = false;

    // ---------- Adding ----------

    /// <summary>Validates dropped paths off the UI thread, then adds the ones that exist.</summary>
    public async Task AddDroppedAsync(IReadOnlyList<string> rawPaths)
    {
        var candidates = new List<string>(rawPaths.Count);
        int rejected = 0;
        foreach (string raw in rawPaths)
        {
            if (FileSystemService.TryNormalize(raw, out string? path))
                candidates.Add(path);
            else
                rejected++;
        }

        List<PathStatus> statuses = await Task.Run(() =>
            candidates.Distinct(FileSystemService.PathComparer).Select(FileSystemService.Inspect).ToList());

        int missing = rejected + statuses.Count(s => s.Kind == PathKind.Missing);
        AddResult result = _shelf.Add(statuses);

        var added = new List<ItemViewModel>(result.Added.Count);
        foreach (StagedItem item in result.Added)
        {
            if (!_byPath.TryGetValue(item.Path, out ItemViewModel? vm))
                continue;
            PathStatus status = statuses.First(s => FileSystemService.PathComparer.Equals(s.Path, item.Path));
            vm.ApplyStatus(status);
            vm.IsHighlighted = true;
            added.Add(vm);
        }
        if (added.Count > 0)
        {
            ItemsAdded?.Invoke(added);
            _ = ClearHighlightsLater(added);
        }

        ReportAdd(added.Count, result.Duplicates, missing);
        if (missing > 0)
            Log.Info($"Drop: {added.Count} added, {result.Duplicates} duplicate, {missing} missing or invalid");
    }

    private void ReportAdd(int added, int duplicates, int missing)
    {
        if (added == 0 && duplicates == 0 && missing == 0)
            return;

        if (added == 0 && missing == 0)
        {
            ShowNotice(duplicates == 1 ? "That item is already on the shelf." : "Those items are already on the shelf.");
            return;
        }
        if (added == 0)
        {
            ShowNotice(missing == 1
                    ? "Couldn't add the item. It may have been moved, deleted or be unreachable."
                    : $"Couldn't add {missing} items. They may have been moved, deleted or be unreachable.",
                NoticeKind.Error);
            return;
        }

        string text = $"Added {Plural(added, "item")}";
        if (duplicates > 0)
            text += $" · {duplicates} already here";
        if (missing > 0)
            text += $" · {missing} couldn't be found";
        ShowNotice(text + ".", missing > 0 ? NoticeKind.Error : NoticeKind.Info);
    }

    private static async Task ClearHighlightsLater(IEnumerable<ItemViewModel> items)
    {
        await Task.Delay(1600);
        foreach (ItemViewModel item in items)
            item.IsHighlighted = false;
    }

    // ---------- Removing ----------

    private void Remove(IReadOnlyCollection<ItemViewModel> items)
    {
        if (items.Count == 0)
            return;
        string? singleName = items.Count == 1 ? items.First().Name : null;
        IReadOnlyList<RemovedEntry> removed = _shelf.Remove(items.Select(i => i.Path).ToList());
        if (removed.Count == 0)
            return;
        _undo = removed;
        ShowNotice(singleName is not null ? $"Removed “{singleName}”." : $"Removed {Plural(removed.Count, "item")}.",
            actionText: "Undo", action: Undo, duration: UndoDuration);
    }

    public void Clear()
    {
        int count = Items.Count;
        if (count == 0)
            return;
        if (_settings().ConfirmClear && ConfirmClear is not null
            && !ConfirmClear($"Remove all {Plural(count, "item")} from the shelf?\n\nYour files stay where they are; only the shelf is cleared."))
            return;

        _undo = _shelf.Clear();
        ShowNotice("Shelf cleared.", actionText: "Undo", action: Undo, duration: UndoDuration);
    }

    private void Undo()
    {
        if (_undo is null)
            return;
        int restored = _shelf.Restore(_undo);
        _undo = null;
        if (restored > 0)
        {
            ShowNotice($"Restored {Plural(restored, "item")}.");
            RefreshAvailability(force: true);
        }
    }

    // ---------- Item actions (explicit user requests only) ----------

    private void Open(IReadOnlyList<ItemViewModel> items)
    {
        if (items is not [{ IsAvailable: true } item])
            return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Open failed for '{item.Path}'", ex);
            ShowNotice($"Couldn't open “{item.Name}”.", NoticeKind.Error);
        }
    }

    private void Reveal(IReadOnlyList<ItemViewModel> items)
    {
        // One Explorer window per containing folder, with the items selected. Capped to avoid a flood of windows.
        foreach (IGrouping<string, ItemViewModel> group in items.Where(i => i.IsAvailable)
                     .GroupBy(i => System.IO.Path.GetDirectoryName(i.Path) ?? i.Path, FileSystemService.PathComparer)
                     .Take(5))
        {
            IntPtr folder = ShellInterop.ILCreateFromPath(group.Key);
            var children = group.Select(i => ShellInterop.ILCreateFromPath(i.Path)).Where(p => p != IntPtr.Zero).ToArray();
            try
            {
                if (folder != IntPtr.Zero)
                    ShellInterop.SHOpenFolderAndSelectItems(folder, (uint)children.Length, children, 0);
            }
            finally
            {
                if (folder != IntPtr.Zero)
                    ShellInterop.ILFree(folder);
                foreach (IntPtr child in children)
                    ShellInterop.ILFree(child);
            }
        }
    }

    private void CopyPaths(IReadOnlyList<ItemViewModel> items)
    {
        string text = string.Join(Environment.NewLine, items.Select(i => i.Path));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                ShowNotice(items.Count == 1 ? "Path copied." : $"{items.Count} paths copied.");
                return;
            }
            catch (COMException)
            {
                // The clipboard is briefly locked by another application; try again.
                Thread.Sleep(50);
            }
        }
        ShowNotice("The clipboard is busy. Try again.", NoticeKind.Error);
    }

    // ---------- Availability ----------

    /// <summary>Re-checks every item on a background thread. Throttled unless forced.</summary>
    public async void RefreshAvailability(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(2))
            return;
        _lastRefresh = DateTime.UtcNow;
        int generation = ++_refreshGeneration;
        List<string> paths = Items.Select(i => i.Path).ToList();

        try
        {
            List<PathStatus> statuses = await Task.Run(() => paths.Select(FileSystemService.Inspect).ToList());
            if (generation != _refreshGeneration)
                return;
            foreach (PathStatus status in statuses)
                ApplyStatus(status);
            UnavailableCount = Items.Count(i => !i.IsAvailable);
        }
        catch (Exception ex)
        {
            Log.Error("Availability check failed", ex);
        }
    }

    /// <summary>Marks paths the shell could not resolve during a drag as unavailable.</summary>
    public void MarkUnavailable(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            ApplyStatus(new PathStatus(path, PathKind.Missing, null));
        UnavailableCount = Items.Count(i => !i.IsAvailable);
    }

    private void ApplyStatus(PathStatus status)
    {
        if (!_byPath.TryGetValue(status.Path, out ItemViewModel? vm))
            return;
        bool wasFolder = vm.IsFolder;
        if (vm.ApplyStatus(status) || wasFolder != vm.IsFolder)
            RequestIcon(vm);
        if (status.Kind != PathKind.Missing && wasFolder != vm.IsFolder)
            _shelf.UpdateKind(vm.Path, vm.IsFolder);
    }

    // ---------- Sync with the model ----------

    /// <summary>Mirrors the shelf's order into <see cref="Items"/>, reusing row view models so icons and selection survive.</summary>
    private void Sync()
    {
        IReadOnlyList<StagedItem> target = _shelf.Items;
        var present = new HashSet<string>(target.Select(i => i.Path), FileSystemService.PathComparer);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Items[i].Path))
            {
                _byPath.Remove(Items[i].Path);
                Items.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (!_byPath.TryGetValue(target[i].Path, out ItemViewModel? vm))
            {
                vm = new ItemViewModel(target[i]);
                _byPath[vm.Path] = vm;
                Items.Insert(i, vm);
                RequestIcon(vm);
                continue;
            }
            int current = Items.IndexOf(vm);
            if (current != i)
                Items.Move(current, i);
        }
        UnavailableCount = Items.Count(i => !i.IsAvailable);
    }

    public void ReloadIcons()
    {
        foreach (ItemViewModel item in Items)
            RequestIcon(item);
    }

    private void RequestIcon(ItemViewModel vm)
    {
        _icons.Request(vm.Path, vm.IsFolder, vm.IsAvailable, IconPixelSize,
            image => _dispatcher.BeginInvoke(() => vm.Icon = image));
    }

    private void OnItemsChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(DragAllText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static List<ItemViewModel> AsItems(object? parameter) => parameter switch
    {
        ItemViewModel item => [item],
        IList list => list.OfType<ItemViewModel>().ToList(),
        _ => [],
    };

    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
