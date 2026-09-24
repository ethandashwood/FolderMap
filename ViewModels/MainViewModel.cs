using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMap.Models;
using FolderMap.Services;

namespace FolderMap.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int SearchLimit = 5000;
    private const int LargeFileLimit = 1000;

    private CancellationTokenSource? _cts;

    // ---------- scanning ----------

    [ObservableProperty]
    private string _scanPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private FolderNode? _root;

    public ObservableCollection<FolderNode> RootItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanActOnSelection))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _status = "Choose a folder or drive and press Scan.";

    // ---------- explorer tab ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPathText))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private FolderNode? _currentFolder;

    public string CurrentPathText => CurrentFolder is null
        ? "Nothing scanned yet"
        : $"{CurrentFolder.FullPath}   ({CurrentFolder.SizeText}, {CurrentFolder.FileCount:N0} files)";

    [ObservableProperty]
    private ObservableCollection<FsNode> _currentItems = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreemap), nameof(IsSunburst))]
    private int _chartMode; // 0 = treemap, 1 = sunburst

    public bool IsTreemap => ChartMode == 0;
    public bool IsSunburst => ChartMode == 1;

    /// <summary>Incremented whenever the tree changes so the charts redraw.</summary>
    [ObservableProperty]
    private int _chartVersion;

    // ---------- selection (shared by every tab) ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText), nameof(CanActOnSelection))]
    private FsNode? _selectedEntry;

    public string SelectedText => SelectedEntry is null
        ? "Nothing selected"
        : $"{SelectedEntry.FullPath}   ({SelectedEntry.SizeText})";

    public bool CanActOnSelection => SelectedEntry != null && !IsBusy;

    // ---------- search tab ----------

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchExtensions = "";
    [ObservableProperty] private decimal? _minSizeMb;
    [ObservableProperty] private decimal? _modifiedWithinDays;
    [ObservableProperty] private bool _includeFolders;
    [ObservableProperty] private ObservableCollection<FsNode> _searchResults = new();
    [ObservableProperty] private string _searchSummary = "Scan a folder, then search by name, type, size or date.";

    // ---------- large files / types / duplicates tabs ----------

    [ObservableProperty] private ObservableCollection<FileNode> _largeFiles = new();
    [ObservableProperty] private ObservableCollection<TypeSummary> _fileTypes = new();
    [ObservableProperty] private decimal? _duplicateMinMb = 1;
    [ObservableProperty] private ObservableCollection<DuplicateRow> _duplicates = new();
    [ObservableProperty] private string _duplicateSummary = "Finds files with identical contents. Online-only OneDrive files are skipped so nothing gets downloaded.";

    // =====================================================================
    // Scanning
    // =====================================================================

    [RelayCommand]
    private async Task Scan()
    {
        var path = ScanPath.Trim().Trim('"');
        if (IsBusy) return;
        if (!Directory.Exists(path))
        {
            Status = $"Folder not found: {path}";
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResetResults();

        var progress = new Progress<ScanProgress>(p =>
            Status = $"Scanning… {p.Folders:N0} folders, {p.Files:N0} files, {Format.Bytes(p.Bytes)}   {p.Current}");

        try
        {
            var timer = Stopwatch.StartNew();
            var token = _cts.Token;
            var root = await Task.Run(() => Scanner.Scan(path, progress, token), token);

            // Summaries are computed off the UI thread too.
            var (large, types, denied) = await Task.Run(() => BuildSummaries(root));

            Root = root;
            RootItems.Add(root);
            CurrentFolder = root;
            LargeFiles = new ObservableCollection<FileNode>(large);
            FileTypes = new ObservableCollection<TypeSummary>(types);
            ChartVersion++;

            Status = $"Scanned {root.FullPath}: {root.FileCount:N0} files, {root.SizeText} in {timer.Elapsed.TotalSeconds:0.0}s"
                     + (denied > 0 ? $"  ·  {denied:N0} folders couldn't be read (permissions)" : "");
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void ResetResults()
    {
        Root = null;
        RootItems.Clear();
        CurrentFolder = null;
        SelectedEntry = null;
        CurrentItems = new();
        SearchResults = new();
        LargeFiles = new();
        FileTypes = new();
        Duplicates = new();
    }

    private static (List<FileNode> Large, List<TypeSummary> Types, int Denied) BuildSummaries(FolderNode root)
    {
        var large = root.AllFiles().OrderByDescending(f => f.Size).Take(LargeFileLimit).ToList();

        double total = Math.Max(1, root.Size);
        var types = root.AllFiles()
            .GroupBy(f => f.Kind)
            .Select(g =>
            {
                long size = g.Sum(f => f.Size);
                return new TypeSummary { Extension = g.Key, Count = g.LongCount(), TotalSize = size, Percent = size * 100.0 / total };
            })
            .OrderByDescending(t => t.TotalSize)
            .ToList();

        int denied = root.Descendants(includeFolders: true).OfType<FolderNode>().Count(f => f.AccessDenied)
                     + (root.AccessDenied ? 1 : 0);

        return (large, types, denied);
    }

    // =====================================================================
    // Explorer navigation
    // =====================================================================

    partial void OnCurrentFolderChanged(FolderNode? value)
    {
        CurrentItems = value is null
            ? new ObservableCollection<FsNode>()
            : new ObservableCollection<FsNode>(value.Children);

        // Expand the tree down to this folder so it's visible.
        for (var p = value?.Parent; p != null; p = p.Parent) p.IsExpanded = true;
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (CurrentFolder?.Parent is { } parent) CurrentFolder = parent;
    }

    private bool CanGoUp() => CurrentFolder?.Parent != null;

    // =====================================================================
    // Search
    // =====================================================================

    [RelayCommand]
    private async Task Search()
    {
        if (Root is null)
        {
            SearchSummary = "Scan a folder first.";
            return;
        }

        var root = Root;
        var nameMatch = BuildNameMatcher(SearchText.Trim());
        var extensions = SearchExtensions
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
        long minBytes = (long)((MinSizeMb ?? 0) * 1024 * 1024);
        DateTime? after = ModifiedWithinDays.HasValue && ModifiedWithinDays.Value > 0
            ? DateTime.Now.AddDays(-(double)ModifiedWithinDays.Value)
            : null;
        bool includeFolders = IncludeFolders && extensions.Count == 0; // folders have no extension

        IsBusy = true;
        try
        {
            var matches = await Task.Run(() => root.Descendants(includeFolders)
                .Where(n => n.Size >= minBytes)
                .Where(n => after is null || n.Modified >= after)
                .Where(n => extensions.Count == 0 || (n is FileNode f && extensions.Contains(f.Extension)))
                .Where(n => nameMatch(n.Name))
                .OrderByDescending(n => n.Size)
                .ToList());

            SearchResults = new ObservableCollection<FsNode>(matches.Take(SearchLimit));
            long totalSize = matches.Sum(m => m is FileNode ? m.Size : 0);
            SearchSummary = matches.Count > SearchLimit
                ? $"{matches.Count:N0} matches ({Format.Bytes(totalSize)} of files). Showing the largest {SearchLimit:N0}."
                : $"{matches.Count:N0} matches ({Format.Bytes(totalSize)} of files).";
        }
        catch (ArgumentException ex)
        {
            SearchSummary = $"Invalid search pattern: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Plain text = "contains". With * or ? it's a wildcard match on the whole name.</summary>
    private static Func<string, bool> BuildNameMatcher(string text)
    {
        if (text.Length == 0) return _ => true;
        if (text.Contains('*') || text.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return name => regex.IsMatch(name);
        }
        return name => name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Duplicates
    // =====================================================================

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (Root is null)
        {
            DuplicateSummary = "Scan a folder first.";
            return;
        }

        var root = Root;
        long minBytes = (long)((DuplicateMinMb ?? 0) * 1024 * 1024);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        var progress = new Progress<string>(s => Status = s);

        try
        {
            var groups = await Task.Run(() => DuplicateFinder.Find(root, minBytes, progress, token), token);

            var rows = new List<DuplicateRow>();
            long wasted = 0;
            int number = 0;
            foreach (var group in groups)
            {
                number++;
                wasted += group[0].Size * (group.Count - 1);
                rows.AddRange(group.Select(f => new DuplicateRow(number, f)));
            }

            Duplicates = new ObservableCollection<DuplicateRow>(rows);
            DuplicateSummary = groups.Count == 0
                ? "No duplicates found."
                : $"{groups.Count:N0} sets of duplicates. {Format.Bytes(wasted)} could be freed by keeping one copy of each.";
            Status = "Duplicate check finished.";
        }
        catch (OperationCanceledException)
        {
            Status = "Duplicate check cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // =====================================================================
    // Keeping the model in sync after file operations
    // =====================================================================

    /// <summary>Call after a file/folder was deleted or moved out of its folder.</summary>
    public void OnRemoved(FsNode node)
    {
        var parent = node.Parent;
        if (parent is null) return;

        bool Gone(FsNode n) => ReferenceEquals(n, node) || (node is FolderNode f && n.IsUnder(f));

        SearchResults = new ObservableCollection<FsNode>(SearchResults.Where(n => !Gone(n)));
        LargeFiles = new ObservableCollection<FileNode>(LargeFiles.Where(n => !Gone(n)));
        Duplicates = new ObservableCollection<DuplicateRow>(Duplicates.Where(r => !Gone(r.File)));

        bool currentGone = CurrentFolder != null && Gone(CurrentFolder);
        bool selectionGone = SelectedEntry != null && Gone(SelectedEntry);
        parent.Remove(node);

        if (selectionGone) SelectedEntry = null;

        if (currentGone) CurrentFolder = parent;
        else RefreshCurrent();
    }

    /// <summary>Call after a successful move. Re-attaches the node if the destination was scanned.</summary>
    public void OnMoved(FsNode node, string destinationFolder)
    {
        OnRemoved(node);
        var destination = Root?.FindFolder(destinationFolder);
        destination?.Add(node);
        RefreshCurrent();
    }

    public void OnRenamed(FsNode node, string newName)
    {
        node.Name = newName;
        OnPropertyChanged(nameof(SelectedText));
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        if (CurrentFolder != null)
            CurrentItems = new ObservableCollection<FsNode>(CurrentFolder.Children);
        OnPropertyChanged(nameof(CurrentPathText));
        ChartVersion++;
    }
}
