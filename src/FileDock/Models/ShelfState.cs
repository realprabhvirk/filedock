namespace FileDock.Models;

/// <summary>Contents of <c>shelf.json</c>.</summary>
public sealed class ShelfState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<StagedItem> Items { get; set; } = [];
}
