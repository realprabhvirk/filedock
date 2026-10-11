using System.IO;
using FileDock.Models;

namespace FileDock.Services;

public sealed record AddResult(IReadOnlyList<StagedItem> Added, int Duplicates);

/// <summary>An item removed from the shelf, with its former position, so the removal can be undone.</summary>
public sealed record RemovedEntry(int Index, StagedItem Item);

/// <summary>
/// The shelf: an ordered, duplicate-free list of path references, saved after every change.
/// The shelf only ever holds references; it never touches the files themselves.
/// </summary>
public sealed class ShelfService
{
    private readonly string _stateFile;
    private readonly List<StagedItem> _items = [];
    private readonly HashSet<string> _keys = new(FileSystemService.PathComparer);
    private bool _saveBlocked;

    public ShelfService(string stateFile)
    {
        _stateFile = stateFile;
    }

    public IReadOnlyList<StagedItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>Raised after any change to the collection.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the state file could not be written.</summary>
    public event Action<Exception>? SaveFailed;

    public LoadResult<ShelfState> Load()
    {
        LoadResult<ShelfState> result = JsonStore.Load<ShelfState>(_stateFile);

        // If the file exists but cannot be read, do not overwrite it later with an empty shelf.
        _saveBlocked = result.Status == LoadStatus.Unreadable;

        _items.Clear();
        _keys.Clear();
        int skipped = 0;
        foreach (StagedItem? item in result.Value.Items ?? [])
        {
            if (item is null || !FileSystemService.TryNormalize(item.Path, out string? path) || !_keys.Add(path))
            {
                skipped++;
                continue;
            }
            _items.Add(new StagedItem { Path = path, IsFolder = item.IsFolder, AddedUtc = item.AddedUtc });
        }
        if (skipped > 0)
            Log.Warn($"Ignored {skipped} invalid or duplicate entries in the saved shelf");

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool Contains(string path) =>
        FileSystemService.TryNormalize(path, out string? normalized) && _keys.Contains(normalized);

    /// <summary>
    /// Appends existing files and folders. Paths already on the shelf, or repeated within the
    /// batch, are counted as duplicates. Missing paths are ignored (callers report them).
    /// </summary>
    public AddResult Add(IEnumerable<PathStatus> entries)
    {
        var added = new List<StagedItem>();
        int duplicates = 0;
        DateTime now = DateTime.UtcNow;

        foreach (PathStatus entry in entries)
        {
            if (entry.Kind == PathKind.Missing || !FileSystemService.TryNormalize(entry.Path, out string? path))
                continue;
            if (!_keys.Add(path))
            {
                duplicates++;
                continue;
            }
            var item = new StagedItem { Path = path, IsFolder = entry.Kind == PathKind.Folder, AddedUtc = now };
            _items.Add(item);
            added.Add(item);
        }

        if (added.Count > 0)
            Commit();
        return new AddResult(added, duplicates);
    }

    public IReadOnlyList<RemovedEntry> Remove(IEnumerable<string> paths)
    {
        var targets = new HashSet<string>(paths, FileSystemService.PathComparer);
        var removed = new List<RemovedEntry>();
        for (int i = 0; i < _items.Count; i++)
        {
            if (targets.Contains(_items[i].Path))
                removed.Add(new RemovedEntry(i, _items[i]));
        }
        if (removed.Count == 0)
            return removed;

        for (int i = removed.Count - 1; i >= 0; i--)
        {
            _items.RemoveAt(removed[i].Index);
            _keys.Remove(removed[i].Item.Path);
        }
        Commit();
        return removed;
    }

    public IReadOnlyList<RemovedEntry> Clear() => Remove(_items.Select(i => i.Path).ToList());

    /// <summary>
    /// Puts previously removed items back at their original positions. Items that were added
    /// again in the meantime are skipped. Returns the number restored.
    /// </summary>
    public int Restore(IReadOnlyList<RemovedEntry> removed)
    {
        int restored = 0;
        foreach (RemovedEntry entry in removed.OrderBy(r => r.Index))
        {
            if (!_keys.Add(entry.Item.Path))
                continue;
            _items.Insert(Math.Min(entry.Index, _items.Count), entry.Item);
            restored++;
        }
        if (restored > 0)
            Commit();
        return restored;
    }

    /// <summary>Records a corrected file/folder flag after re-inspection (for example, a path that was replaced).</summary>
    public void UpdateKind(string path, bool isFolder)
    {
        StagedItem? item = _items.Find(i => FileSystemService.PathComparer.Equals(i.Path, path));
        if (item is null || item.IsFolder == isFolder)
            return;
        item.IsFolder = isFolder;
        Save();
    }

    public bool Save()
    {
        if (_saveBlocked)
            return false;
        try
        {
            JsonStore.Save(_stateFile, new ShelfState { Items = [.. _items] });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save the shelf", ex);
            SaveFailed?.Invoke(ex);
            return false;
        }
    }

    private void Commit()
    {
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
