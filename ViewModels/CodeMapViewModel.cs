using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderMap.Models;
using FolderMap.Services;

namespace FolderMap.ViewModels;

/// <summary>One line in the "Uses" / "Used by" lists, e.g. "calls  Order.Save()".</summary>
public sealed class LinkItem
{
    public LinkItem(string verb, CodeSymbol other, int count)
    {
        Verb = verb;
        Other = other;
        Count = count;
    }

    public string Verb { get; }
    public CodeSymbol Other { get; }
    public int Count { get; }
    public string Text => $"{Verb}  {Other.DisplayName}" + (Count > 1 ? $"  ×{Count}" : "");
    public string Where => Other.Location;
}

/// <summary>One step in a call path, e.g. "2. Save() calls Validate()".</summary>
public sealed class PathStep
{
    public PathStep(int number, CodeLink link)
    {
        Number = number;
        Link = link;
    }

    public int Number { get; }
    public CodeLink Link { get; }
    public CodeSymbol Target => Link.To;
    public string Text => $"{Number}. {Link.From.ShortName} {Link.Verb} {Link.To.DisplayName}";
    public string Where => Link.To.Location;
}

public partial class CodeMapViewModel : ObservableObject
{
    private const int MaxOverviewNodes = 250; // more than this gets unreadable (and slow)
    private const int MaxFocusNodes = 400;
    private const int MaxListItems = 5000;
    internal const int MaxPreviewLines = 150;

    private readonly IReadOnlyList<string> _files;
    private readonly IReadOnlyList<string>? _markupFiles;
    private readonly Dictionary<string, string[]> _fileLines = new(StringComparer.OrdinalIgnoreCase);
    private bool _showingPath; // the graph is showing a call path instead of the normal view
    private readonly string? _focusFile;
    private readonly CancellationTokenSource _cts = new();
    private CodeGraph? _graph;
    private bool _suspendRebuild; // set while changing several settings at once

    public CodeMapViewModel(string folderPath, IReadOnlyList<string> files, string? focusFile,
        IReadOnlyList<string>? markupFiles = null)
    {
        FolderPath = folderPath;
        _files = files;
        _markupFiles = markupFiles;
        _focusFile = focusFile;
        var name = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        Title = $"Code map · {(name.Length > 0 ? name : folderPath)}";
    }

    public string FolderPath { get; }

    [ObservableProperty] private string _title = "Code map";
    [ObservableProperty] private string _status = "Analysing…";
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _viewNote = "";

    /// <summary>What the graph control draws.</summary>
    [ObservableProperty] private GraphView? _view;

    // ---------- filters ----------

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showMethods = true;
    [ObservableProperty] private bool _showVariables = true;
    [ObservableProperty] private bool _showClasses = true;
    [ObservableProperty] private bool _showUnconnected;

    /// <summary>0 = everything, 1 = selected + neighbours, 2 = selected + 2 steps.</summary>
    [ObservableProperty] private int _focusMode;

    [ObservableProperty] private ObservableCollection<CodeSymbol> _listItems = new();

    /// <summary>0 = everything, 1 = only things that look unused.</summary>
    [ObservableProperty] private int _listMode;
    [ObservableProperty] private string _unusedLabel = "Possibly unused";

    partial void OnListModeChanged(int value) => RefreshList();
    partial void OnSearchTextChanged(string value) => RefreshList();
    partial void OnShowMethodsChanged(bool value) => FiltersChanged();
    partial void OnShowVariablesChanged(bool value) => FiltersChanged();
    partial void OnShowClassesChanged(bool value) => FiltersChanged();
    partial void OnShowUnconnectedChanged(bool value) => RebuildView();
    partial void OnFocusModeChanged(int value) => RebuildView();

    private void FiltersChanged()
    {
        RefreshList();
        RebuildView();
    }

    // ---------- selection + details ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedDetails), nameof(CanFindPath))]
    private CodeSymbol? _selected;

    public bool HasSelection => Selected != null;
    public string SelectedTitle => Selected?.DisplayName ?? "Nothing selected";

    public string SelectedDetails => Selected is null
        ? "Click a dot in the graph or a name in the list to see what it's connected to."
        : $"{Selected.KindText} · {Selected.FileName}, {Selected.LinesText}"
          + (Selected.UnusedReason is { } reason
              ? $"\n⚠ Possibly unused: {reason} (in this folder). It may still be used by code outside it, by reflection, or by a framework."
              : "");

    // ---------- code preview ----------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreview))] private string _codePreview = "";
    [ObservableProperty] private string _previewHeader = "";
    public bool HasPreview => CodePreview.Length > 0;

    /// <summary>The background file read for the preview (exposed so tests can wait for it).</summary>
    internal Task PreviewTask { get; private set; } = Task.CompletedTask;

    // ---------- call paths ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathStart), nameof(PathStartText), nameof(CanFindPath))]
    private CodeSymbol? _pathStart;

    [ObservableProperty] private string _pathSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPathSteps))] private ObservableCollection<PathStep> _pathSteps = new();

    public bool HasPathStart => PathStart != null;
    public string PathStartText => PathStart is null ? "" : $"Path from {PathStart.DisplayName}";
    public bool CanFindPath => PathStart != null && Selected != null && !ReferenceEquals(PathStart, Selected);
    public bool HasPathSteps => PathSteps.Count > 0;

    [ObservableProperty] private ObservableCollection<LinkItem> _uses = new();
    [ObservableProperty] private ObservableCollection<LinkItem> _usedBy = new();
    [ObservableProperty] private string _usesHeader = "";
    [ObservableProperty] private string _usedByHeader = "";

    partial void OnSelectedChanged(CodeSymbol? value)
    {
        UpdateDetails();
        PreviewTask = LoadPreviewAsync(value);
        // In focus mode the graph follows the selection. In overview mode, make sure the
        // selected item is actually on screen.
        if (FocusMode > 0 || (value != null && View != null && !View.Nodes.Contains(value)))
            RebuildView();
    }

    // =====================================================================

    public async Task LoadAsync()
    {
        var progress = new Progress<string>(s => Status = s);
        var files = _files;
        var token = _cts.Token;

        try
        {
            var markup = _markupFiles;
            _graph = await Task.Run(() => CodeAnalyzer.Analyze(files, progress, token, markup), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Status = $"Analysis failed: {ex.Message}";
            IsBusy = false;
            return;
        }

        var g = _graph;
        int callables = g.Symbols.Count(s => s.IsCallable);
        int variables = g.Symbols.Count(s => s.IsVariable);
        int classes = g.Symbols.Count(s => s.Kind == CodeKind.Class);
        int connections = g.Links.Count(l => l.Kind != LinkKind.Contains);
        Summary = $"{g.FileCount:N0} files · {classes:N0} classes · {callables:N0} methods/functions · "
                  + $"{variables:N0} variables · {connections:N0} connections";
        Status = g.Notes.Count == 0 ? "Ready" : string.Join("  ·  ", g.Notes.Take(3));
        IsBusy = false;

        int unused = g.Symbols.Count(s => s.IsPossiblyUnused);
        UnusedLabel = $"Possibly unused ({unused:N0})";
        RefreshList();

        // Opened from a single file: start focused on the busiest thing in that file.
        if (_focusFile != null)
        {
            var start = g.Symbols
                .Where(s => string.Equals(s.File, _focusFile, StringComparison.OrdinalIgnoreCase) && s.Degree > 0)
                .OrderByDescending(s => s.Degree)
                .FirstOrDefault();
            if (start != null)
            {
                _suspendRebuild = true; // rebuild once below, not after each change
                FocusMode = 1;
                Selected = start;
                _suspendRebuild = false;
            }
        }

        RebuildView();
    }

    public void Cancel() => _cts.Cancel();

    private bool KindVisible(CodeSymbol s) => s.Kind switch
    {
        CodeKind.Class => ShowClasses,
        CodeKind.Property or CodeKind.Field or CodeKind.Variable => ShowVariables,
        _ => ShowMethods,
    };

    private bool LinkVisible(CodeLink l) =>
        (l.Kind != LinkKind.Contains || ShowClasses) && KindVisible(l.From) && KindVisible(l.To);

    private void RefreshList()
    {
        if (_graph is null) return;
        var text = SearchText.Trim();
        IEnumerable<CodeSymbol> items = _graph.Symbols
            .Where(KindVisible)
            .Where(s => text.Length == 0
                        || s.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || s.FileName.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (ListMode == 1)
        {
            // Unused view: grouped by file, in the order they appear, so it reads like a to-do list.
            items = items.Where(s => s.IsPossiblyUnused)
                .OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Line);
        }
        items = items.Take(MaxListItems);
        ListItems = new ObservableCollection<CodeSymbol>(items);
    }

    private void RebuildView()
    {
        if (_graph is null || _suspendRebuild || _showingPath) return;

        HashSet<CodeSymbol> nodes;
        string note;

        if (FocusMode > 0 && Selected != null)
        {
            // Walk outwards from the selection, one step per focus level.
            nodes = new HashSet<CodeSymbol> { Selected };
            var frontier = new List<CodeSymbol> { Selected };
            for (int step = 0; step < FocusMode && nodes.Count < MaxFocusNodes; step++)
            {
                var next = new List<CodeSymbol>();
                foreach (var s in frontier)
                {
                    foreach (var link in s.Outgoing.Concat(s.Incoming))
                    {
                        if (!LinkVisible(link)) continue;
                        var other = ReferenceEquals(link.From, s) ? link.To : link.From;
                        if (nodes.Count < MaxFocusNodes && nodes.Add(other)) next.Add(other);
                    }
                }
                frontier = next;
            }
            note = $"{Selected.DisplayName} and everything within {FocusMode} step{(FocusMode > 1 ? "s" : "")}";
        }
        else
        {
            var candidates = _graph.Symbols
                .Where(KindVisible)
                .Where(s => ShowUnconnected || s.Degree > 0)
                .ToList();

            if (candidates.Count > MaxOverviewNodes)
            {
                nodes = candidates.OrderByDescending(s => s.Degree).Take(MaxOverviewNodes).ToHashSet();
                note = $"Showing the {MaxOverviewNodes} most connected of {candidates.Count:N0}. "
                       + "Select something and choose \"Selected + neighbours\" to explore the rest.";
            }
            else
            {
                nodes = candidates.ToHashSet();
                note = FocusMode > 0 ? "Select something to focus on it." : "";
            }
            if (Selected != null) nodes.Add(Selected);
        }

        var links = _graph.Links
            .Where(l => nodes.Contains(l.From) && nodes.Contains(l.To) && LinkVisible(l))
            .ToList();

        View = new GraphView(nodes.ToList(), links);
        ViewNote = note.Length > 0 ? note : $"{nodes.Count:N0} items · {links.Count:N0} connections";
    }

    private void UpdateDetails()
    {
        var s = Selected;
        if (s is null)
        {
            Uses = new();
            UsedBy = new();
            UsesHeader = UsedByHeader = "";
            return;
        }

        Uses = new ObservableCollection<LinkItem>(s.Outgoing
            .OrderBy(l => l.Kind).ThenBy(l => l.To.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(l => new LinkItem(l.Kind == LinkKind.Contains ? "has" : l.Verb, l.To, l.Count)));

        UsedBy = new ObservableCollection<LinkItem>(s.Incoming
            .OrderBy(l => l.Kind).ThenBy(l => l.From.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(l => new LinkItem(l.PassiveVerb, l.From, l.Count)));

        UsesHeader = s.Kind == CodeKind.Class ? $"Members and uses ({Uses.Count})" : $"Uses ({Uses.Count})";
        UsedByHeader = $"Used by ({UsedBy.Count})";
    }

    // =====================================================================
    // Code preview
    // =====================================================================

    private async Task LoadPreviewAsync(CodeSymbol? symbol)
    {
        if (symbol is null)
        {
            CodePreview = PreviewHeader = "";
            return;
        }

        if (!_fileLines.TryGetValue(symbol.File, out var lines))
        {
            try
            {
                lines = await Task.Run(() => File.ReadAllLines(symbol.File));
                _fileLines[symbol.File] = lines;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(Selected, symbol))
                {
                    PreviewHeader = "Code";
                    CodePreview = $"Couldn't read the file: {ex.Message}";
                }
                return;
            }
        }

        if (!ReferenceEquals(Selected, symbol)) return; // selection moved on while reading
        CodePreview = FormatPreview(lines, symbol.Line, symbol.EndLine, out var range);
        PreviewHeader = $"Code · {range} of {symbol.FileName}";
    }

    /// <summary>
    /// The lines from first to last (1-based) with line numbers, common indentation removed
    /// and tabs expanded. Long bodies are cut at <see cref="MaxPreviewLines"/>.
    /// </summary>
    internal static string FormatPreview(string[] lines, int first, int last, out string range)
    {
        if (lines.Length == 0)
        {
            range = "empty file";
            return "";
        }

        first = Math.Clamp(first, 1, lines.Length);
        last = Math.Clamp(Math.Max(last, first), first, lines.Length);
        int shownLast = Math.Min(last, first + MaxPreviewLines - 1);

        var slice = lines[(first - 1)..shownLast].Select(l => l.Replace("\t", "    ")).ToList();
        int indent = slice.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        int width = shownLast.ToString().Length;

        var sb = new StringBuilder();
        for (int i = 0; i < slice.Count; i++)
        {
            var line = slice[i];
            var text = line.Length >= indent && line[..indent].Trim().Length == 0 ? line[indent..] : line.TrimStart();
            sb.Append((first + i).ToString().PadLeft(width)).Append("  ").AppendLine(text);
        }
        if (shownLast < last) sb.Append($"… {last - shownLast:N0} more lines (open it to see the rest)");

        range = first == last ? $"line {first}" : $"lines {first}–{last}";
        return sb.ToString().TrimEnd();
    }

    // =====================================================================
    // Call paths
    // =====================================================================

    /// <summary>Step 1: remember the selected item as where the path starts.</summary>
    public void StartPath()
    {
        if (Selected is null) return;
        PathStart = Selected;
        PathSteps = new();
        PathSummary = "Now select where the path should end (in the graph or the list), then press \"Find path to selected\".";
    }

    /// <summary>Step 2: find the shortest chain from the start to the selected item and show it.</summary>
    public void FindPathToSelected()
    {
        if (PathStart is not { } start || Selected is not { } end || ReferenceEquals(start, end)) return;

        var path = CodeGraph.FindPath(start, end);
        string intro;
        if (path != null)
        {
            intro = $"{start.DisplayName} reaches {end.DisplayName} in {Steps(path.Count)}:";
        }
        else if ((path = CodeGraph.FindPath(end, start)) != null)
        {
            intro = $"Nothing leads from {start.DisplayName} to {end.DisplayName}, but it works the other way round, "
                    + $"in {Steps(path.Count)}:";
        }
        else
        {
            PathSteps = new();
            PathSummary = $"Nothing connects {start.DisplayName} and {end.DisplayName} in either direction "
                          + "(in the code that was analysed).";
            return;
        }

        PathSteps = new ObservableCollection<PathStep>(path.Select((link, i) => new PathStep(i + 1, link)));
        PathSummary = intro;

        // Show just the path in the graph.
        var nodes = new List<CodeSymbol> { path[0].From };
        nodes.AddRange(path.Select(l => l.To));
        _showingPath = true;
        View = new GraphView(nodes, path);
        ViewNote = $"Call path: {string.Join(" → ", nodes.Select(n => n.ShortName))}   (press Clear path to go back)";
    }

    /// <summary>Forget the path and go back to the normal graph.</summary>
    public void ClearPath()
    {
        PathStart = null;
        PathSteps = new();
        PathSummary = "";
        _showingPath = false;
        RebuildView();
    }

    private static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";
}
