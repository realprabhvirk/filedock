namespace FileDock.Models;

/// <summary>
/// A persisted reference to a file or folder on the shelf. FileDock never stores
/// file content, only the original path.
/// </summary>
public sealed class StagedItem
{
    public string Path { get; set; } = "";

    public bool IsFolder { get; set; }

    public DateTime AddedUtc { get; set; }
}
