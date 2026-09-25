using FolderMap.Models;
using FolderMap.Services;

namespace FolderMap.Tests;

public class ScannerTests
{
    [Fact]
    public void Sizes_and_file_counts_add_up_through_the_tree()
    {
        using var t = new TempFolder();
        t.File("a.bin", 1000);
        t.File("docs/b.bin", 2000);
        t.File("docs/deep/c.bin", 3000);
        t.Folder("empty");

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(6000, root.Size);
        Assert.Equal(3, root.FileCount);
        var docs = root.Folders.Single(f => f.Name == "docs");
        Assert.Equal(5000, docs.Size);
        Assert.Equal(2, docs.FileCount);
        Assert.Equal(0, root.Folders.Single(f => f.Name == "empty").Size);
    }

    [Fact]
    public void Folders_and_files_are_sorted_largest_first()
    {
        using var t = new TempFolder();
        t.File("small/x.bin", 10);
        t.File("big/x.bin", 5000);
        t.File("medium/x.bin", 500);
        t.File("f1.bin", 1);
        t.File("f2.bin", 300);

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(new[] { "big", "medium", "small" }, root.Folders.Select(f => f.Name));
        Assert.Equal(new[] { "f2.bin", "f1.bin" }, root.Files.Select(f => f.Name));
    }

    [Fact]
    public void Full_paths_point_at_real_files()
    {
        using var t = new TempFolder();
        var created = t.File("one/two/three.txt", 5);

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);
        var file = root.AllFiles().Single();

        Assert.Equal(Path.GetFullPath(created), Path.GetFullPath(file.FullPath));
        Assert.True(File.Exists(file.FullPath));
    }

    [Fact]
    public void Unicode_and_space_names_are_handled()
    {
        using var t = new TempFolder();
        t.File("Fotos 2024 – été/naïve café 日本.jpg", 42);

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(42, root.Size);
        Assert.True(File.Exists(root.AllFiles().Single().FullPath));
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_scanned()
    {
        using var t = new TempFolder();
        var parts = Enumerable.Range(0, 12).Select(i => $"folder_with_a_fairly_long_name_{i:00}");
        var relative = Path.Combine(parts.Append("deep.bin").ToArray());
        Assert.True(Path.Combine(t.Path, relative).Length > 260);
        t.File(relative, 77);

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(77, root.Size);
    }

    [Fact]
    public void Symbolic_links_to_folders_are_not_followed()
    {
        using var t = new TempFolder();
        t.File("real/data.bin", 1000);
        try
        {
            Directory.CreateSymbolicLink(t.Combine("link"), t.Combine("real"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // creating links needs admin or Developer Mode on Windows - nothing to test
        }

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(1000, root.Size); // not 2000 - the link didn't count the data twice
    }

    [Fact]
    public void A_cancelled_scan_throws()
    {
        using var t = new TempFolder();
        t.File("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Scanner.Scan(t.Path, null, cts.Token));
    }

    [Fact]
    public void Progress_is_reported()
    {
        using var t = new TempFolder();
        for (int i = 0; i < 20; i++) t.File($"d{i}/f.bin", 10);
        var reports = new List<ScanProgress>();

        Scanner.Scan(t.Path, new SyncProgress<ScanProgress>(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(20, reports[^1].Files);
    }
}

public class TreeTests
{
    private static (FolderNode root, FolderNode sub, FileNode file) Build()
    {
        var root = new FolderNode("C:\\data", null);
        var sub = new FolderNode("sub", root);
        var file = new FileNode("a.txt", sub, 100, DateTime.Now, FileAttributes.Normal);
        sub.Files.Add(file);
        sub.Size = 100;
        sub.FileCount = 1;
        root.Folders.Add(sub);
        root.Size = 100;
        root.FileCount = 1;
        return (root, sub, file);
    }

    [Fact]
    public void Removing_a_file_updates_every_ancestor()
    {
        var (root, sub, file) = Build();

        sub.Remove(file);

        Assert.Equal(0, sub.Size);
        Assert.Equal(0, root.Size);
        Assert.Equal(0, root.FileCount);
        Assert.Null(file.Parent);
    }

    [Fact]
    public void Adding_keeps_sizes_and_sort_order()
    {
        var (root, sub, _) = Build();
        var bigger = new FileNode("big.txt", sub, 500, DateTime.Now, FileAttributes.Normal);

        sub.Add(bigger);

        Assert.Equal(600, root.Size);
        Assert.Equal(2, root.FileCount);
        Assert.Same(bigger, sub.Files[0]);
    }

    [Fact]
    public void Renaming_a_folder_changes_the_paths_below_it()
    {
        var (_, sub, file) = Build();

        sub.Name = "renamed";

        Assert.Equal(Path.Combine("C:\\data", "renamed", "a.txt"), file.FullPath);
    }

    [Fact]
    public void FindFolder_finds_nested_folders_ignoring_case_and_rejects_outside_paths()
    {
        using var t = new TempFolder();
        t.File("One/Two/x.bin", 1);
        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal("Two", root.FindFolder(Path.Combine(t.Path, "one", "TWO"))?.Name);
        Assert.Same(root, root.FindFolder(t.Path));
        Assert.Null(root.FindFolder(Path.GetTempPath()));
        Assert.Null(root.FindFolder(Path.Combine(t.Path, "missing")));
    }

    [Fact]
    public void IsUnder_only_matches_real_ancestors()
    {
        var (root, sub, file) = Build();
        var other = new FolderNode("other", root);

        Assert.True(file.IsUnder(root));
        Assert.True(file.IsUnder(sub));
        Assert.False(file.IsUnder(other));
        Assert.False(root.IsUnder(root));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(-5, "0 B")]
    public void Bytes_are_formatted_readably(long bytes, string expected)
    {
        // Decimal separator depends on the PC's language settings.
        Assert.Equal(expected.Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator),
                     Format.Bytes(bytes));
    }
}

/// <summary>IProgress that runs immediately (Progress&lt;T&gt; posts to another thread).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
