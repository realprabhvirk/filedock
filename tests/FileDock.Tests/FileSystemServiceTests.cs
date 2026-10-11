using FileDock.Services;

namespace FileDock.Tests;

public class FileSystemServiceTests
{
    [Theory]
    [InlineData(@"C:\Docs\report.pdf", @"C:\Docs\report.pdf")]
    [InlineData(@"c:\docs\report.pdf", @"C:\docs\report.pdf")]
    [InlineData(@"C:\Docs\", @"C:\Docs")]
    [InlineData(@"C:\Docs\\\", @"C:\Docs")]
    [InlineData(@"C:/Docs/report.pdf", @"C:\Docs\report.pdf")]
    [InlineData(@"C:\Docs\..\Other\.\a.txt", @"C:\Other\a.txt")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\?\C:\Docs\a.txt", @"C:\Docs\a.txt")]
    [InlineData(@"\\?\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder")]
    public void Normalizes_valid_paths(string raw, string expected)
    {
        Assert.True(FileSystemService.TryNormalize(raw, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("report.pdf")]
    [InlineData(@"Docs\report.pdf")]
    [InlineData(@"C:report.pdf")]
    [InlineData(@"\Docs\report.pdf")]
    [InlineData(@"C:\Docs\*.pdf")]
    [InlineData(@"C:\Docs\re?ort.pdf")]
    [InlineData(@"C:\Docs\a|b.txt")]
    [InlineData("C:\\Docs\\a\0b.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"https://example.com/file.pdf")]
    public void Rejects_invalid_or_unsafe_paths(string? raw)
    {
        Assert.False(FileSystemService.TryNormalize(raw, out _));
    }

    [Fact]
    public void Duplicate_detection_is_case_insensitive()
    {
        FileSystemService.TryNormalize(@"C:\Docs\Report.PDF", out string? a);
        FileSystemService.TryNormalize(@"c:\docs\report.pdf\", out string? b);
        Assert.True(FileSystemService.PathComparer.Equals(a, b));
    }

    [Fact]
    public void Inspect_reports_files_folders_and_missing_paths()
    {
        using var temp = new TempDirectory();
        string file = temp.File("a.txt", "hello");
        string folder = temp.Folder("sub");

        PathStatus fileStatus = FileSystemService.Inspect(file);
        Assert.Equal(PathKind.File, fileStatus.Kind);
        Assert.Equal(5, fileStatus.Size);

        Assert.Equal(PathKind.Folder, FileSystemService.Inspect(folder).Kind);
        Assert.Equal(PathKind.Missing, FileSystemService.Inspect(System.IO.Path.Combine(temp.Path, "gone.txt")).Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\Sam\Downloads\report.pdf", "report.pdf", "Downloads")]
    [InlineData(@"C:\Users\Sam\Downloads", "Downloads", "Sam")]
    [InlineData(@"C:\report.pdf", "report.pdf", @"C:\")]
    [InlineData(@"C:\", @"C:\", "")]
    public void Display_and_location_names(string path, string name, string location)
    {
        Assert.Equal(name, FileSystemService.GetDisplayName(path));
        Assert.Equal(location, FileSystemService.GetLocationName(path));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(5602, "5.5 KB")]
    [InlineData(240_002, "234 KB")]
    [InlineData(2_516_582, "2.4 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void Formats_sizes_compactly(long bytes, string expected)
    {
        Assert.Equal(expected, FileSystemService.FormatSize(bytes));
    }
}
