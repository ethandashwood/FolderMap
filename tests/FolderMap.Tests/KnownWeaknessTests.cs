using FolderMap.Models;
using FolderMap.Services;
using FolderMap.ViewModels;

namespace FolderMap.Tests;

/// <summary>
/// Regression tests: each one reproduces a weakness found in the code review (2026-09-25).
/// They were all fixed, so they should all PASS now. If one fails, that weakness is back.
/// </summary>
[Trait("Category", "Regression")]
public class FixedWeaknessTests
{
    private static async Task<MainViewModel> Scanned(TempFolder t)
    {
        var vm = new MainViewModel { ScanPath = t.Path };
        await vm.ScanCommand.ExecuteAsync(null);
        return vm;
    }

    // Fixed review finding M1: a huge number in "At least (MB)" overflows and the exception escapes.
    [Fact]
    public async Task Search_with_an_enormous_minimum_size_does_not_crash()
    {
        using var t = new TempFolder();
        t.File("a.bin", 10);
        var vm = await Scanned(t);

        vm.MinSizeMb = 99_999_999_999_999m;
        var error = await Record.ExceptionAsync(() => vm.SearchCommand.ExecuteAsync(null));

        Assert.Null(error);
    }

    // Fixed review finding M1: same problem in the Duplicates tab.
    [Fact]
    public async Task Duplicate_search_with_an_enormous_minimum_size_does_not_crash()
    {
        using var t = new TempFolder();
        t.File("a.bin", 10);
        var vm = await Scanned(t);

        vm.DuplicateMinMb = 99_999_999_999_999m;
        var error = await Record.ExceptionAsync(() => vm.FindDuplicatesCommand.ExecuteAsync(null));

        Assert.Null(error);
    }

    // Fixed review finding M1: a huge day count is reported as an "invalid search pattern".
    [Fact]
    public async Task Search_with_an_enormous_day_count_gives_a_sensible_message()
    {
        using var t = new TempFolder();
        t.File("a.bin", 10);
        var vm = await Scanned(t);

        vm.ModifiedWithinDays = 99_999_999m;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.DoesNotContain("pattern", vm.SearchSummary);
    }

    // Fixed review finding M6: Windows file names ignore case, so "photo.JPG" -> "photo.jpg"
    // is refused as "already exists". (Passes on Linux/Mac, where case matters.)
    [Fact]
    public void Renaming_to_change_only_capitalisation_works()
    {
        using var t = new TempFolder();
        t.File("photo.JPG", 10);
        var node = Scanner.Scan(t.Path, null, CancellationToken.None).AllFiles().Single();

        FileOps.Rename(node, "photo.jpg");

        Assert.Contains("photo.jpg", Directory.GetFiles(t.Path).Select(Path.GetFileName));
    }

    // Fixed review finding L5: a JavaScript regex containing a backtick (/`/) is read as the start
    // of a template string, so everything after it in the file is ignored.
    [Fact]
    public void JavaScript_regex_literals_with_quotes_do_not_hide_the_rest_of_the_file()
    {
        using var t = new TempFolder();
        t.Text("app.js", """
            function first() { second(); }
            const tick = /`/;
            function second() { first(); }
            """);

        var g = CodeAnalyzerTests.Analyze(t);

        Assert.Contains(g.Symbols, s => s.Name == "second");
        CodeAnalyzerTests.AssertLink(g, "second", "first", LinkKind.Calls);
    }

    // Fixed review finding M5: nothing stops you recycling every copy of a duplicate.
    // The view model should refuse (or at least flag) removing the last remaining copy.
    [Fact]
    public async Task Duplicates_tab_knows_when_only_one_copy_is_left()
    {
        using var t = new TempFolder();
        t.File("a/pic.jpg", 5000, 3);
        t.File("b/pic.jpg", 5000, 3);
        var vm = await Scanned(t);
        vm.DuplicateMinMb = 0;
        await vm.FindDuplicatesCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Duplicates.Count);

        vm.OnRemoved(vm.Duplicates[0].File);

        // After one copy is gone, the survivor is no longer a duplicate and drops off the list,
        // so it can't be recycled from there by mistake.
        Assert.Empty(vm.Duplicates);
    }

    // Fixed review finding M4: "C:" means the whole drive, not the current folder on C.
    [Fact]
    public void Typing_a_bare_drive_letter_means_the_whole_drive()
    {
        if (!OperatingSystem.IsWindows()) return; // drive letters are a Windows thing

        Assert.Equal(@"C:\", Scanner.NormalizeRoot("C:"));
        Assert.Equal(@"D:\", Scanner.NormalizeRoot(" \"D:\" "));
        Assert.Equal(@"C:\Users", Scanner.NormalizeRoot(@"C:\Users\"));
    }

    // Fixed review finding M3: very wide folders show a capped list plus a "more" line in the tree.
    [Fact]
    public void The_tree_caps_very_wide_folders()
    {
        var root = new FolderNode("C:\\wide", null);
        for (int i = 0; i < FolderNode.MaxTreeChildren + 100; i++)
            root.Folders.Add(new FolderNode($"f{i}", root));

        var shown = root.TreeChildren.ToList();

        Assert.Equal(FolderNode.MaxTreeChildren + 1, shown.Count);
        var more = Assert.IsType<MoreFoldersItem>(shown[^1]);
        Assert.Equal(100, more.Count);
    }

    // Fixed review finding M1: number boxes are clamped instead of overflowing.
    [Fact]
    public void Huge_or_negative_numbers_are_clamped()
    {
        Assert.Equal(0, MainViewModel.MegabytesToBytes(null));
        Assert.Equal(0, MainViewModel.MegabytesToBytes(-5));
        Assert.Equal(1024 * 1024, MainViewModel.MegabytesToBytes(1));
        Assert.True(MainViewModel.MegabytesToBytes(decimal.MaxValue) > 0);
        Assert.Null(MainViewModel.ModifiedAfter(0));
        Assert.NotNull(MainViewModel.ModifiedAfter(decimal.MaxValue));
    }

    // Fixed review finding L1: acting on something deleted since the scan gives a clear message.
    [Fact]
    public void Acting_on_a_file_deleted_since_the_scan_says_so()
    {
        using var t = new TempFolder();
        var path = t.File("gone.txt", 5);
        var node = Scanner.Scan(t.Path, null, CancellationToken.None).AllFiles().Single();
        File.Delete(path);

        var error = Assert.Throws<FileNotFoundException>(() => FileOps.Rename(node, "new.txt"));
        Assert.Contains("scan", error.Message);
    }

    // Fixed review finding F2 / M6: names Windows can't store are refused up front.
    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("ends with dot.")]
    [InlineData("ends with space ")]
    [InlineData("a:b.txt")]
    [InlineData("what?.txt")]
    [InlineData("   ")]
    public void Names_windows_cannot_store_are_refused(string name)
    {
        Assert.NotNull(FileOps.ValidateName(name));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("Photo 2024 (1).JPG")]
    [InlineData("naïve café.txt")]
    [InlineData(".gitignore")]
    public void Normal_names_are_allowed(string name)
    {
        Assert.Null(FileOps.ValidateName(name));
    }

    // Fixed review finding H2: files that RUN when opened are recognised, so the app asks first.
    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("backup.BAT", true)]
    [InlineData("script.ps1", true)]
    [InlineData("tool.py", true)]
    [InlineData("app.js", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.txt", false)]
    [InlineData("Program.cs", false)]
    public void Programs_and_scripts_are_recognised(string name, bool runnable)
    {
        Assert.Equal(runnable, FileOps.IsRunnable(name));
    }

    // Fixed review finding L4: minified/bundled files are skipped instead of analysed as noise.
    [Fact]
    public void Minified_files_are_skipped_with_a_note()
    {
        using var t = new TempFolder();
        t.Text("vendor.js", "function a(){" + new string('x', 5000) + "}");
        t.Text("app.js", "function main() { helper(); }\nfunction helper() {}");

        var g = CodeAnalyzerTests.Analyze(t);

        Assert.Contains(g.Notes, n => n.Contains("vendor.js"));
        CodeAnalyzerTests.AssertLink(g, "main", "helper", LinkKind.Calls);
    }

    // Fixed review finding L4: division isn't mistaken for a regex, and regexes don't hide code.
    [Fact]
    public void JavaScript_division_and_regexes_are_told_apart()
    {
        using var t = new TempFolder();
        t.Text("math.js", """
            function half(x) { return x / 2 / 1; }
            function check(s) { return /["'`]/.test(s) && half(4); }
            function after() { check("a"); }
            """);

        var g = CodeAnalyzerTests.Analyze(t);

        CodeAnalyzerTests.AssertLink(g, "check", "half", LinkKind.Calls);
        CodeAnalyzerTests.AssertLink(g, "after", "check", LinkKind.Calls);
    }
}
