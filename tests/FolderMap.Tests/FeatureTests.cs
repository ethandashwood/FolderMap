using Avalonia;
using FolderMap.Controls;
using FolderMap.Models;
using FolderMap.Services;
using FolderMap.ViewModels;

namespace FolderMap.Tests;

public class DuplicateFinderTests
{
    private static List<List<FileNode>> Find(TempFolder t, long minSize = 1) =>
        DuplicateFinder.Find(Scanner.Scan(t.Path, null, CancellationToken.None), minSize, null, CancellationToken.None);

    [Fact]
    public void Identical_files_are_grouped()
    {
        using var t = new TempFolder();
        t.File("a/photo.jpg", 5000, fill: 7);
        t.File("b/photo copy.jpg", 5000, fill: 7);
        t.File("c/other.jpg", 5000, fill: 8);

        var groups = Find(t);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void Same_size_files_that_differ_only_after_64KB_are_not_duplicates()
    {
        using var t = new TempFolder();
        var one = new byte[100_000];
        var two = new byte[100_000];
        two[^1] = 1; // same first 64 KB, different ending
        t.File("one.bin", one);
        t.File("two.bin", two);

        Assert.Empty(Find(t));
    }

    [Fact]
    public void Minimum_size_and_empty_files_are_respected()
    {
        using var t = new TempFolder();
        t.File("small1.txt", 100);
        t.File("small2.txt", 100);
        t.File("empty1.txt", 0);
        t.File("empty2.txt", 0);

        Assert.Empty(Find(t, minSize: 1000));
        Assert.Single(Find(t, minSize: 0)); // the two 100-byte files; empty files never count
    }

    [Fact]
    public void Biggest_wasted_space_comes_first()
    {
        using var t = new TempFolder();
        t.File("s1.bin", 1000, 1);
        t.File("s2.bin", 1000, 1);
        t.File("b1.bin", 9000, 2);
        t.File("b2.bin", 9000, 2);

        var groups = Find(t);

        Assert.Equal(9000, groups[0][0].Size);
    }
}

public class FileOpsTests
{
    private static FileNode ScanSingleFile(TempFolder t) =>
        Scanner.Scan(t.Path, null, CancellationToken.None).AllFiles().Single();

    [Fact]
    public void Rename_renames_on_disk()
    {
        using var t = new TempFolder();
        t.File("old.txt", 3);
        var node = ScanSingleFile(t);

        FileOps.Rename(node, "new.txt");

        Assert.True(File.Exists(t.Combine("new.txt")));
        Assert.False(File.Exists(t.Combine("old.txt")));
    }

    [Theory]
    [InlineData("bad/name.txt")]
    [InlineData("nul\0.txt")]
    public void Rename_rejects_invalid_names(string name)
    {
        using var t = new TempFolder();
        t.File("old.txt", 3);

        Assert.Throws<ArgumentException>(() => FileOps.Rename(ScanSingleFile(t), name));
        Assert.True(File.Exists(t.Combine("old.txt")));
    }

    [Fact]
    public void Rename_never_overwrites_an_existing_file()
    {
        using var t = new TempFolder();
        t.File("a.txt", 3);
        t.File("b.txt", 999);
        var a = Scanner.Scan(t.Path, null, CancellationToken.None).AllFiles().Single(f => f.Name == "a.txt");

        Assert.Throws<IOException>(() => FileOps.Rename(a, "b.txt"));
        Assert.Equal(999, new FileInfo(t.Combine("b.txt")).Length);
    }

    [Fact]
    public void Move_moves_a_file_and_never_overwrites()
    {
        using var t = new TempFolder();
        t.File("src/report.txt", 10);
        t.File("dest/report.txt", 55);
        t.Folder("other");
        var root = Scanner.Scan(t.Path, null, CancellationToken.None);
        var report = root.AllFiles().Single(f => f.Parent!.Name == "src");

        Assert.Throws<IOException>(() => FileOps.Move(report, t.Combine("dest")));
        Assert.Equal(55, new FileInfo(t.Combine("dest/report.txt")).Length);

        FileOps.Move(report, t.Combine("other"));
        Assert.True(File.Exists(t.Combine("other/report.txt")));
        Assert.False(File.Exists(t.Combine("src/report.txt")));
    }
}

public class MainViewModelTests
{
    private static async Task<MainViewModel> Scanned(TempFolder t)
    {
        var vm = new MainViewModel { ScanPath = t.Path };
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Root);
        return vm;
    }

    [Fact]
    public async Task Scan_fills_the_explorer_and_summary_tabs()
    {
        using var t = new TempFolder();
        t.File("a.jpg", 300);
        t.File("b.txt", 100);

        var vm = await Scanned(t);

        Assert.Same(vm.Root, vm.CurrentFolder);
        Assert.Equal(2, vm.CurrentItems.Count);
        Assert.Equal("a.jpg", vm.LargeFiles[0].Name);
        Assert.Contains(vm.FileTypes, x => x.Extension == ".jpg");
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Scan_of_a_missing_folder_reports_it()
    {
        var vm = new MainViewModel { ScanPath = Path.Combine(Path.GetTempPath(), "definitely_not_here_" + Guid.NewGuid()) };

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Null(vm.Root);
        Assert.Contains("not found", vm.Status);
    }

    [Theory]
    [InlineData("invoice", "", 2)]      // "contains", ignoring case
    [InlineData("INV*.pdf", "", 1)]     // wildcard, whole name
    [InlineData("", "pdf", 2)]          // extension filter
    [InlineData("", ".txt, pdf", 3)]    // several extensions
    [InlineData("nothing", "", 0)]
    public async Task Search_filters_by_name_and_extension(string text, string extensions, int expected)
    {
        using var t = new TempFolder();
        t.File("Invoice-01.pdf", 10);
        t.File("old/my invoice.txt", 10);
        t.File("notes.pdf", 10);
        t.File("photo.jpg", 10);
        var vm = await Scanned(t);

        vm.SearchText = text;
        vm.SearchExtensions = extensions;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.SearchResults.Count);
    }

    [Fact]
    public async Task Search_minimum_size_filter_works()
    {
        using var t = new TempFolder();
        t.File("big.bin", 2 * 1024 * 1024);
        t.File("small.bin", 1000);
        var vm = await Scanned(t);

        vm.MinSizeMb = 1;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("big.bin", Assert.Single(vm.SearchResults).Name);
    }

    [Fact]
    public async Task Removing_a_folder_updates_totals_and_every_list()
    {
        using var t = new TempFolder();
        t.File("keep/a.bin", 100);
        t.File("drop/b.bin", 5000);
        var vm = await Scanned(t);
        var drop = vm.Root!.Folders.Single(f => f.Name == "drop");
        vm.SelectedEntry = drop.Files[0];

        vm.OnRemoved(drop);

        Assert.Equal(100, vm.Root.Size);
        Assert.DoesNotContain(vm.LargeFiles, f => f.Name == "b.bin");
        Assert.Null(vm.SelectedEntry);
    }

    [Fact]
    public async Task Analyse_code_button_is_enabled_only_for_code()
    {
        using var t = new TempFolder();
        t.Text("src/app.py", "print('hi')");
        t.File("pics/cat.jpg", 10);
        var vm = await Scanned(t);

        vm.SelectedEntry = vm.Root!.Folders.Single(f => f.Name == "src");
        await vm.CodeCheck; // the check runs in the background so the window never freezes
        Assert.True(vm.CanAnalyseCode);

        vm.SelectedEntry = vm.Root.Folders.Single(f => f.Name == "pics");
        await vm.CodeCheck;
        Assert.False(vm.CanAnalyseCode);
    }
}

public class SquarifyTests
{
    [Fact]
    public void Areas_match_values_stay_inside_and_never_overlap()
    {
        var values = new List<double> { 6, 6, 4, 3, 2, 2, 1 };
        var bounds = new Rect(10, 20, 6, 4); // area 24 = sum of values

        var rects = Squarify.Layout(values, bounds);

        for (int i = 0; i < rects.Length; i++)
        {
            Assert.Equal(values[i], rects[i].Width * rects[i].Height, 6);
            Assert.True(bounds.Inflate(0.0001).Contains(rects[i]), $"rect {i} is outside the bounds");
            for (int j = i + 1; j < rects.Length; j++)
            {
                var overlap = rects[i].Intersect(rects[j]);
                Assert.True(overlap.Width * overlap.Height < 1e-6, $"rects {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Empty_or_zero_input_gives_empty_rects()
    {
        Assert.Empty(Squarify.Layout(new List<double>(), new Rect(0, 0, 10, 10)));
        Assert.All(Squarify.Layout(new List<double> { 1, 2 }, new Rect(0, 0, 0, 10)), r => Assert.Equal(default, r));
    }
}
