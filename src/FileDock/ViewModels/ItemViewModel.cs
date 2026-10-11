using System.IO;
using System.Windows.Media;
using FileDock.Models;
using FileDock.Services;

namespace FileDock.ViewModels;

/// <summary>One row on the shelf.</summary>
public sealed class ItemViewModel : ObservableObject
{
    private bool _isFolder;
    private bool _isAvailable = true;
    private long? _size;
    private ImageSource? _icon;
    private bool _isHighlighted;

    public ItemViewModel(StagedItem item)
    {
        Path = item.Path;
        _isFolder = item.IsFolder;
        Name = FileSystemService.GetDisplayName(item.Path);
        Location = FileSystemService.GetLocationName(item.Path);
    }

    public string Path { get; }

    public string Name { get; }

    public string Location { get; }

    public bool IsFolder => _isFolder;

    /// <summary>False when the original has been moved, deleted or disconnected.</summary>
    public bool IsAvailable => _isAvailable;

    public string Details
    {
        get
        {
            var parts = new List<string>(3);
            if (!_isAvailable)
                parts.Add("Unavailable");
            else if (_isFolder)
                parts.Add("Folder");
            else
            {
                string ext = System.IO.Path.GetExtension(Path).TrimStart('.');
                parts.Add(ext.Length is > 0 and <= 12 ? ext.ToUpperInvariant() : "File");
                if (_size is long size)
                    parts.Add(FileSystemService.FormatSize(size));
            }
            if (Location.Length > 0)
                parts.Add(Location);
            return string.Join("  ·  ", parts);
        }
    }

    public string AutomationName => $"{Name}, {Details}";

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>Briefly true after the item was added, to play the "added" highlight.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    /// <summary>Applies fresh file system information. Returns true if availability changed.</summary>
    public bool ApplyStatus(PathStatus status)
    {
        bool available = status.Kind != PathKind.Missing;
        bool availabilityChanged = available != _isAvailable;
        _isAvailable = available;
        if (available)
            _isFolder = status.Kind == PathKind.Folder;
        _size = status.Size;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(AutomationName));
        return availabilityChanged;
    }
}
