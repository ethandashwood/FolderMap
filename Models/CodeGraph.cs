namespace FolderMap.Models;

public enum CodeKind { Class, Constructor, Method, Function, Property, Field, Variable }

public enum LinkKind { Calls, Creates, Reads, Writes, Contains }

/// <summary>A method, function, variable or class found in the analysed code.</summary>
public sealed class CodeSymbol
{
    public CodeSymbol(string id, string name, CodeKind kind, string? container, string file, int line, string language)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Container = container;
        File = file;
        Line = line;
        EndLine = line;
        Language = language;
    }

    public string Id { get; }
    public string Name { get; }
    public CodeKind Kind { get; }
    /// <summary>The class (or outer function / module) it belongs to.</summary>
    public string? Container { get; }
    public string File { get; }
    public int Line { get; }
    /// <summary>Last line of its body (same as <see cref="Line"/> for variables and one-liners).</summary>
    public int EndLine { get; internal set; }
    public string Language { get; }

    /// <summary>
    /// Used by something the analysis can't see: a framework, an attribute ([Fact], [RelayCommand]),
    /// an override, a decorator, an export, or a name referenced from XAML/HTML.
    /// Such things are never reported as unused.
    /// </summary>
    public bool UsedImplicitly { get; internal set; }

    /// <summary>Why this looks unused ("nothing calls it"...), or null if it's used.</summary>
    public string? UnusedReason { get; internal set; }
    public bool IsPossiblyUnused => UnusedReason != null;

    public List<CodeLink> Outgoing { get; } = new();
    public List<CodeLink> Incoming { get; } = new();

    /// <summary>Number of calls / reads / writes / creates in and out (class membership not counted).</summary>
    public int Degree { get; internal set; }

    public bool IsCallable => Kind is CodeKind.Method or CodeKind.Function or CodeKind.Constructor;
    public bool IsVariable => Kind is CodeKind.Property or CodeKind.Field or CodeKind.Variable;

    public string ShortName => IsCallable ? Name + "()" : Name;
    public string DisplayName => Container is null ? ShortName : $"{Container}.{ShortName}";
    public string FileName => Path.GetFileName(File);
    public string Location => $"{FileName}:{Line}";

    public string KindText => Kind switch
    {
        CodeKind.Class => "class",
        CodeKind.Constructor => "constructor",
        CodeKind.Method => "method",
        CodeKind.Function => "function",
        CodeKind.Property => "property",
        CodeKind.Field => "field",
        _ => "variable",
    };

    public string LinesText => EndLine > Line ? $"lines {Line}–{EndLine}" : $"line {Line}";

    public string Subtitle => Container is null
        ? $"{KindText} · {Location}"
        : $"{KindText} in {Container} · {Location}";

    // ----- layout state, used only by CodeGraphControl -----
    internal double X, Y, VX, VY;
    internal bool Placed;

    public override string ToString() => DisplayName;
}

/// <summary>A directed connection, e.g. "Save() calls Validate()" or "Save() writes _count".</summary>
public sealed class CodeLink
{
    public CodeLink(CodeSymbol from, CodeSymbol to, LinkKind kind)
    {
        From = from;
        To = to;
        Kind = kind;
    }

    public CodeSymbol From { get; }
    public CodeSymbol To { get; }
    public LinkKind Kind { get; }
    /// <summary>How many times this connection appears in the code.</summary>
    public int Count { get; internal set; } = 1;

    public string Verb => Kind switch
    {
        LinkKind.Calls => "calls",
        LinkKind.Creates => "creates",
        LinkKind.Reads => "reads",
        LinkKind.Writes => "writes",
        _ => "contains",
    };

    public string PassiveVerb => Kind switch
    {
        LinkKind.Calls => "called by",
        LinkKind.Creates => "created by",
        LinkKind.Reads => "read by",
        LinkKind.Writes => "written by",
        _ => "member of",
    };
}

/// <summary>Everything found in one analysis run.</summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, CodeSymbol> _byId = new();
    private readonly Dictionary<(CodeSymbol, CodeSymbol, LinkKind), CodeLink> _linkIndex = new();

    public List<CodeSymbol> Symbols { get; } = new();
    public List<CodeLink> Links { get; } = new();
    /// <summary>Problems worth telling the user about (unreadable files, limits hit...).</summary>
    public List<string> Notes { get; } = new();
    public int FileCount { get; set; }

    public CodeSymbol GetOrAdd(string id, Func<CodeSymbol> create)
    {
        if (_byId.TryGetValue(id, out var existing)) return existing;
        var symbol = create();
        _byId[id] = symbol;
        Symbols.Add(symbol);
        return symbol;
    }

    public void Link(CodeSymbol from, CodeSymbol to, LinkKind kind)
    {
        if (ReferenceEquals(from, to)) return; // recursion / self-reference isn't interesting here

        var key = (from, to, kind);
        if (_linkIndex.TryGetValue(key, out var existing))
        {
            existing.Count++;
            return;
        }
        var link = new CodeLink(from, to, kind);
        _linkIndex[key] = link;
        Links.Add(link);
        from.Outgoing.Add(link);
        to.Incoming.Add(link);
    }

    /// <summary>Names mentioned in XAML / HTML files, e.g. Click="OnSaveClick" or {Binding Total}.</summary>
    public HashSet<string> MarkupNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Call once analysis is finished.</summary>
    public void Finish()
    {
        foreach (var s in Symbols)
        {
            s.Degree = s.Outgoing.Count(l => l.Kind != LinkKind.Contains)
                     + s.Incoming.Count(l => l.Kind != LinkKind.Contains);
            if (MarkupNames.Contains(s.Name)) s.UsedImplicitly = true;
            s.UnusedReason = WhyUnused(s);
        }
        Symbols.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "Possibly unused" rules. Classes and constructors are left out: they're often created by
    /// frameworks, dependency injection or reflection, so flagging them would mostly be wrong.
    /// </summary>
    private static string? WhyUnused(CodeSymbol s)
    {
        if (s.UsedImplicitly || s.Name == "(top-level code)") return null;

        if (s.Kind is CodeKind.Method or CodeKind.Function)
        {
            bool called = s.Incoming.Any(l => l.Kind is LinkKind.Calls or LinkKind.Creates);
            return called ? null : "nothing calls it";
        }

        if (s.IsVariable)
        {
            bool read = s.Incoming.Any(l => l.Kind == LinkKind.Reads);
            if (read) return null;
            bool written = s.Incoming.Any(l => l.Kind == LinkKind.Writes);
            return written ? "only ever set, never read" : "never used";
        }
        return null;
    }

    /// <summary>
    /// The shortest chain of calls / creations / reads / writes leading from one symbol to another
    /// (breadth-first search), or null if nothing connects them in that direction.
    /// </summary>
    public static List<CodeLink>? FindPath(CodeSymbol from, CodeSymbol to)
    {
        if (ReferenceEquals(from, to)) return new List<CodeLink>();

        var cameFrom = new Dictionary<CodeSymbol, CodeLink>();
        var visited = new HashSet<CodeSymbol> { from };
        var queue = new Queue<CodeSymbol>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var link in current.Outgoing)
            {
                if (link.Kind == LinkKind.Contains || !visited.Add(link.To)) continue;
                cameFrom[link.To] = link;
                if (ReferenceEquals(link.To, to))
                {
                    // Walk back from the end to rebuild the path in order.
                    var path = new List<CodeLink>();
                    for (var node = to; !ReferenceEquals(node, from); node = cameFrom[node].From)
                        path.Add(cameFrom[node]);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(link.To);
            }
        }
        return null;
    }
}

/// <summary>The subset of the graph currently drawn.</summary>
public sealed class GraphView
{
    public GraphView(IReadOnlyList<CodeSymbol> nodes, IReadOnlyList<CodeLink> links)
    {
        Nodes = nodes;
        Links = links;
    }

    public IReadOnlyList<CodeSymbol> Nodes { get; }
    public IReadOnlyList<CodeLink> Links { get; }
}

/// <summary>One file ("script") in the Boxes view, with the classes, methods and variables it contains.</summary>
public sealed class FileBox
{
    public FileBox(string file, string folder, string language)
    {
        File = file;
        Folder = folder;
        Language = language;
    }

    public string File { get; }
    public string Name => Path.GetFileName(File);
    /// <summary>Folder relative to the analysed folder ("" for the top level).</summary>
    public string Folder { get; }
    public string Language { get; }

    /// <summary>Rows shown in the box: classes, each followed by its members, then everything else.</summary>
    public List<CodeSymbol> Rows { get; } = new();
    /// <summary>How many more symbols the file has than fit in the box.</summary>
    public int HiddenRows { get; internal set; }
    public int SymbolCount { get; internal set; }

    // ----- layout state, used only by FileBoxesControl -----
    internal double X, Y, W, H;

    public override string ToString() => Name;
}

/// <summary>"File A uses file B" - every call / creation / read / write from A's code into B's.</summary>
public sealed class FileEdge
{
    public FileEdge(FileBox from, FileBox to)
    {
        From = from;
        To = to;
    }

    public FileBox From { get; }
    public FileBox To { get; }
    public List<CodeLink> Links { get; } = new();
    public int Count => Links.Sum(l => l.Count);
}

/// <summary>Everything the Boxes view draws.</summary>
public sealed class FileMap
{
    public List<FileBox> Boxes { get; } = new();
    public List<FileEdge> Edges { get; } = new();

    public FileBox? BoxOf(CodeSymbol symbol) =>
        Boxes.FirstOrDefault(b => string.Equals(b.File, symbol.File, StringComparison.OrdinalIgnoreCase));
}
