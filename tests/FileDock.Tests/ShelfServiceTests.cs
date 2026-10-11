using System.IO;
using FileDock.Models;
using FileDock.Services;

namespace FileDock.Tests;

public sealed class ShelfServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _stateFile;

    public ShelfServiceTests()
    {
        _stateFile = Path.Combine(_temp.Path, "state", "shelf.json");
    }

    public void Dispose() => _temp.Dispose();

    private ShelfService NewShelf()
    {
        var shelf = new ShelfService(_stateFile);
        shelf.Load();
        return shelf;
    }

    private static PathStatus FileAt(string path) => new(path, PathKind.File, 1);

    private static string[] Names(ShelfService shelf) => shelf.Items.Select(i => Path.GetFileName(i.Path)).ToArray();

    [Fact]
    public void New_batches_are_appended_to_the_existing_shelf()
    {
        ShelfService shelf = NewShelf();
        shelf.Add([FileAt(@"C:\Downloads\a.pdf"), FileAt(@"C:\Downloads\b.pdf")]);
        shelf.Add([FileAt(@"C:\Documents\c.docx")]);

        Assert.Equal(["a.pdf", "b.pdf", "c.docx"], Names(shelf));
    }

    [Fact]
    public void Duplicate_paths_are_not_added_twice()
    {
        ShelfService shelf = NewShelf();
        shelf.Add([FileAt(@"C:\Downloads\a.pdf")]);

        AddResult again = shelf.Add([FileAt(@"c:\downloads\A.PDF"), FileAt(@"C:\Downloads\b.pdf"), FileAt(@"C:\Downloads\b.pdf")]);

        Assert.Equal(2, shelf.Count);
        Assert.Single(again.Added);
        Assert.Equal(2, again.Duplicates);
    }

    [Fact]
    public void Missing_and_invalid_paths_are_never_added()
    {
        ShelfService shelf = NewShelf();
        AddResult result = shelf.Add([
            new PathStatus(@"C:\gone.txt", PathKind.Missing, null),
            new PathStatus("relative.txt", PathKind.File, 1),
            FileAt(@"C:\ok.txt"),
        ]);

        Assert.Equal(["ok.txt"], Names(shelf));
        Assert.Single(result.Added);
    }

    [Fact]
    public void Folders_are_recorded_as_folders()
    {
        ShelfService shelf = NewShelf();
        shelf.Add([new PathStatus(@"C:\Projects\Site", PathKind.Folder, null)]);
        Assert.True(shelf.Items[0].IsFolder);
    }

    [Fact]
    public void Remove_then_restore_puts_items_back_in_their_original_positions()
    {
        ShelfService shelf = NewShelf();
        shelf.Add(new[] { "a", "b", "c", "d", "e" }.Select(n => FileAt($@"C:\x\{n}.txt")));

        IReadOnlyList<RemovedEntry> removed = shelf.Remove([@"C:\x\b.txt", @"C:\X\D.TXT"]);
        Assert.Equal(["a.txt", "c.txt", "e.txt"], Names(shelf));

        Assert.Equal(2, shelf.Restore(removed));
        Assert.Equal(["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"], Names(shelf));
    }

    [Fact]
    public void Clear_can_be_undone()
    {
        ShelfService shelf = NewShelf();
        shelf.Add([FileAt(@"C:\x\a.txt"), FileAt(@"C:\x\b.txt")]);

        IReadOnlyList<RemovedEntry> removed = shelf.Clear();
        Assert.Equal(0, shelf.Count);

        shelf.Restore(removed);
        Assert.Equal(["a.txt", "b.txt"], Names(shelf));
    }

    [Fact]
    public void Restore_skips_items_that_were_added_again_meanwhile()
    {
        ShelfService shelf = NewShelf();
        shelf.Add([FileAt(@"C:\x\a.txt")]);
        IReadOnlyList<RemovedEntry> removed = shelf.Clear();
        shelf.Add([FileAt(@"C:\x\a.txt")]);

        Assert.Equal(0, shelf.Restore(removed));
        Assert.Equal(1, shelf.Count);
    }

    [Fact]
    public void Removing_from_the_shelf_never_touches_the_file()
    {
        string file = _temp.File("keep.txt", "content");
        ShelfService shelf = NewShelf();
        shelf.Add([FileSystemService.Inspect(file)]);

        shelf.Remove([file]);
        shelf.Clear();

        Assert.True(File.Exists(file));
        Assert.Equal("content", File.ReadAllText(file));
    }

    [Fact]
    public void Shelf_survives_a_restart()
    {
        ShelfService first = NewShelf();
        first.Add([FileAt(@"C:\x\a.txt"), new PathStatus(@"C:\x\Folder", PathKind.Folder, null)]);

        ShelfService second = NewShelf();

        Assert.Equal(["a.txt", "Folder"], Names(second));
        Assert.True(second.Items[1].IsFolder);
        Assert.False(File.Exists(_stateFile + ".tmp"));
    }

    [Fact]
    public void First_run_starts_empty()
    {
        var shelf = new ShelfService(_stateFile);
        LoadResult<ShelfState> result = shelf.Load();
        Assert.Equal(LoadStatus.NotFound, result.Status);
        Assert.Equal(0, shelf.Count);
    }

    [Fact]
    public void Corrupt_state_is_preserved_and_the_shelf_starts_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "{ \"Items\": [ { \"Path\": ");

        var shelf = new ShelfService(_stateFile);
        LoadResult<ShelfState> result = shelf.Load();

        Assert.Equal(LoadStatus.Corrupt, result.Status);
        Assert.Equal(0, shelf.Count);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal("{ \"Items\": [ { \"Path\": ", File.ReadAllText(result.BackupPath!));

        // The shelf keeps working and saves normally afterwards.
        shelf.Add([FileAt(@"C:\x\a.txt")]);
        Assert.Equal(1, NewShelf().Count);
    }

    [Fact]
    public void Wrong_json_shape_is_treated_as_corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "[1, 2, 3]");

        LoadResult<ShelfState> result = new ShelfService(_stateFile).Load();

        Assert.Equal(LoadStatus.Corrupt, result.Status);
    }

    [Fact]
    public void Invalid_and_duplicate_entries_in_saved_state_are_skipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, """
            {
              "Version": 1,
              "Items": [
                { "Path": "C:\\x\\a.txt" },
                { "Path": "c:\\X\\A.TXT" },
                { "Path": "relative\\b.txt" },
                { "Path": "" },
                null,
                { "Path": "C:\\x\\c.txt", "IsFolder": false }
              ]
            }
            """);

        ShelfService shelf = NewShelf();

        Assert.Equal(["a.txt", "c.txt"], Names(shelf));
    }
}
