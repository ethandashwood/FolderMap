using System.Text;
using System.Text.RegularExpressions;
using FolderMap.Models;

namespace FolderMap.Services;

/// <summary>
/// Lightweight analysis for Python and JavaScript/TypeScript. It doesn't run a full
/// compiler, so it matches names by pattern:
///   1. strip comments and string contents (so text inside them isn't mistaken for code),
///   2. find classes, functions, methods and variables and which lines belong to each,
///   3. scan each body for calls (foo(), self.foo(), this.foo()), object creation and
///      variable reads/writes, then match those names to the declarations from step 2.
/// It's right most of the time, but can miss dynamic calls or link same-named methods wrongly.
/// </summary>
internal static class PatternCodeAnalyzer
{
    // Every pattern gives up after 1 second on a single line of text, so an unusual file
    // can't hang the analysis. That line is then skipped.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Lines this long are almost always minified or bundled code, not something a person wrote.
    private const int MinifiedLineLength = 3000;

    // =====================================================================
    // Shared pieces
    // =====================================================================

    /// <summary>One run of code text and who it belongs to.</summary>
    private sealed record Segment(CodeSymbol? Owner, string File, string? ClassName, string Text, string? SkipName);

    /// <summary>Per-language regexes used when resolving references.</summary>
    private sealed record Syntax(
        string Lang,
        Regex SelfMember,     // self.x / this.x, group 1 = name, group 2 = "(" if it's a call
        Regex BareCall,       // foo(
        Regex OtherCall,      // something.foo(   (not self/this)
        Regex Identifier,     // plain names, for module-level variables
        Regex WriteAfter,     // what follows a name when it's being assigned
        string? CtorName,               // null = constructors are named after the class (Java)
        HashSet<string> Keywords,
        bool ImplicitThis = false);     // Java/Kotlin: "count" inside a method can mean this.count

    /// <summary>Lookup tables of everything declared, used to resolve names.</summary>
    private sealed class Index
    {
        private readonly CodeGraph _graph;
        private readonly string _lang;
        private readonly Dictionary<string, CodeSymbol> _classes = new();
        private readonly Dictionary<(string File, string Name), CodeSymbol> _fileFunctions = new();
        private readonly Dictionary<string, List<CodeSymbol>> _functionsByName = new();
        private readonly Dictionary<(string Cls, string Name), CodeSymbol> _methods = new();
        private readonly Dictionary<string, List<CodeSymbol>> _methodsByName = new();
        private readonly Dictionary<(string Cls, string Name), CodeSymbol> _fields = new();
        private readonly Dictionary<(string File, string Name), CodeSymbol> _moduleVars = new();
        private readonly HashSet<string> _filesWithVars = new();

        public Index(CodeGraph graph, string lang)
        {
            _graph = graph;
            _lang = lang;
        }

        private CodeSymbol Add(string key, string name, CodeKind kind, string? container, string file, int line)
        {
            var id = $"{_lang}:{file}|{kind}|{container}|{name}|{key}";
            return _graph.GetOrAdd(id, () => new CodeSymbol(id, name, kind, container, file, line, _lang));
        }

        private static void AddToList(Dictionary<string, List<CodeSymbol>> map, string name, CodeSymbol s)
        {
            if (!map.TryGetValue(name, out var list)) map[name] = list = new List<CodeSymbol>();
            if (!list.Contains(s)) list.Add(s);
        }

        public CodeSymbol Class(string file, string name, int line)
        {
            var s = Add("", name, CodeKind.Class, null, file, line);
            _classes.TryAdd(name, s);
            return s;
        }

        public CodeSymbol Method(string file, string cls, string name, int line, bool isConstructor)
        {
            var s = Add("", name, isConstructor ? CodeKind.Constructor : CodeKind.Method, cls, file, line);
            _methods.TryAdd((cls, name), s);
            if (!isConstructor) AddToList(_methodsByName, name, s);
            if (_classes.TryGetValue(cls, out var owner)) _graph.Link(owner, s, LinkKind.Contains);
            return s;
        }

        public CodeSymbol Function(string file, string? container, string name, int line)
        {
            var s = Add("", name, CodeKind.Function, container, file, line);
            _fileFunctions.TryAdd((file, name), s);
            AddToList(_functionsByName, name, s);
            return s;
        }

        public CodeSymbol Field(string file, string cls, string name, int line)
        {
            if (_fields.TryGetValue((cls, name), out var existing)) return existing;
            var s = Add("", name, CodeKind.Field, cls, file, line);
            _fields[(cls, name)] = s;
            if (_classes.TryGetValue(cls, out var owner)) _graph.Link(owner, s, LinkKind.Contains);
            return s;
        }

        public CodeSymbol ModuleVariable(string file, string name, int line)
        {
            if (_moduleVars.TryGetValue((file, name), out var existing)) return existing;
            var s = Add("", name, CodeKind.Variable, Path.GetFileNameWithoutExtension(file), file, line);
            _moduleVars[(file, name)] = s;
            _filesWithVars.Add(file);
            return s;
        }

        public CodeSymbol TopLevel(string file) =>
            Add("", "(top-level code)", CodeKind.Function, Path.GetFileNameWithoutExtension(file), file, 1);

        public CodeSymbol? MethodIn(string? cls, string name) =>
            cls != null && _methods.TryGetValue((cls, name), out var s) ? s : null;

        public CodeSymbol? FieldIn(string? cls, string name) =>
            cls != null && _fields.TryGetValue((cls, name), out var s) ? s : null;

        public CodeSymbol? ClassNamed(string name) => _classes.TryGetValue(name, out var s) ? s : null;

        public CodeSymbol? FunctionInFile(string file, string name) =>
            _fileFunctions.TryGetValue((file, name), out var s) ? s : null;

        public CodeSymbol? ModuleVariableIn(string file, string name) =>
            _moduleVars.TryGetValue((file, name), out var s) ? s : null;

        /// <summary>Only link by name alone when exactly one thing in the project has that name.</summary>
        public CodeSymbol? UniqueFunction(string name) =>
            _functionsByName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;

        public CodeSymbol? UniqueMethod(string name) =>
            _methodsByName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;

        public bool HasModuleVariables(string file) => _filesWithVars.Contains(file);
    }

    private static string ReadFile(string path, CodeGraph graph)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            graph.Notes.Add($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            return "";
        }
    }

    /// <summary>Turn every reference found in a segment into links.</summary>
    private static void Resolve(Segment seg, Index index, Syntax syntax, CodeGraph graph)
    {
        var text = seg.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        CodeSymbol From() => seg.Owner ?? index.TopLevel(seg.File);

        // self.x / this.x
        foreach (Match m in syntax.SelfMember.Matches(text))
        {
            var name = m.Groups[1].Value;
            bool isCall = m.Groups[2].Success;
            if (isCall)
            {
                var target = index.MethodIn(seg.ClassName, name) ?? index.UniqueMethod(name);
                if (target != null) Link(From(), target, LinkKind.Calls);
            }
            else if (index.FieldIn(seg.ClassName, name) is { } field)
            {
                bool write = syntax.WriteAfter.IsMatch(text, m.Index + m.Length);
                Link(From(), field, write ? LinkKind.Writes : LinkKind.Reads);
            }
            else if (index.MethodIn(seg.ClassName, name) is { } methodRef)
            {
                Link(From(), methodRef, LinkKind.Calls); // passed as a callback
            }
        }

        // foo(...)  - a function in this file, a class being created, or a uniquely-named function elsewhere
        foreach (Match m in syntax.BareCall.Matches(text))
        {
            var name = m.Groups[1].Value;
            if (syntax.Keywords.Contains(name)) continue;

            if (syntax.ImplicitThis && index.MethodIn(seg.ClassName, name) is { } own)
                Link(From(), own, LinkKind.Calls); // save() inside a class = this.save()
            else if (index.FunctionInFile(seg.File, name) is { } local)
                Link(From(), local, LinkKind.Calls);
            else if (index.ClassNamed(name) is { } cls)
                Link(From(), index.MethodIn(name, syntax.CtorName ?? name) ?? cls, LinkKind.Creates);
            else if (index.UniqueFunction(name) is { } other)
                Link(From(), other, LinkKind.Calls);
        }

        // obj.foo(...) or module.foo(...)
        foreach (Match m in syntax.OtherCall.Matches(text))
        {
            var name = m.Groups[1].Value;
            var target = index.UniqueMethod(name) ?? index.UniqueFunction(name);
            if (target != null) Link(From(), target, LinkKind.Calls);
        }

        // Plain names: module-level variables being read or written, and functions passed
        // around without calling them (callbacks like list.map(format), exports, event handlers).
        foreach (Match m in syntax.Identifier.Matches(text))
        {
            var name = m.Groups[1].Value;
            if (name == seg.SkipName || syntax.Keywords.Contains(name)) continue;

            if (index.ModuleVariableIn(seg.File, name) is { } variable)
            {
                bool write = syntax.WriteAfter.IsMatch(text, m.Index + m.Length);
                Link(From(), variable, write ? LinkKind.Writes : LinkKind.Reads);
            }
            else if (syntax.ImplicitThis && index.FieldIn(seg.ClassName, name) is { } field)
            {
                bool write = syntax.WriteAfter.IsMatch(text, m.Index + m.Length)
                             || Regex.IsMatch(text[(m.Index + m.Length)..], @"^\s*(?:\+\+|--)")
                             || (m.Index >= 2 && text.Substring(m.Index - 2, 2) is "++" or "--");
                Link(From(), field, write ? LinkKind.Writes : LinkKind.Reads);
            }
            else if (index.FunctionInFile(seg.File, name) is { } function)
            {
                Link(From(), function, LinkKind.Calls);
            }
        }

        void Link(CodeSymbol from, CodeSymbol to, LinkKind kind)
        {
            // Don't count a declaration line as the owner "writing" its own new variable.
            if (kind == LinkKind.Writes && to.Name == seg.SkipName) return;
            graph.Link(from, to, kind);
        }
    }

    private static void ResolveAll(List<Segment> segments, Index index, Syntax syntax, CodeGraph graph, CancellationToken token)
    {
        foreach (var seg in segments)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Resolve(seg, index, syntax, graph);
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological text - skip this line rather than hang.
            }
        }
    }

    private static void Blank(StringBuilder sb, char c) => sb.Append(c == '\n' ? '\n' : ' ');

    /// <summary>Minified or bundled files have enormous lines; analysing them just adds noise.</summary>
    internal static bool LooksMinified(string source)
    {
        int lineStart = 0;
        for (int i = 0; i <= source.Length; i++)
        {
            if (i == source.Length || source[i] == '\n')
            {
                if (i - lineStart > MinifiedLineLength) return true;
                lineStart = i + 1;
            }
        }
        return false;
    }

    // =====================================================================
    // Python
    // =====================================================================

    private static readonly Regex PyDef = new(@"^(?:async\s+)?def\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PyClass = new(@"^class\s+([A-Za-z_]\w*)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PyAssign = new(@"^([A-Za-z_]\w*)\s*(?::[^=]+)?=(?!=)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PySelfAssign = new(@"\b(?:self|cls)\.([A-Za-z_]\w*)\s*(?:[+\-*/%&|^@]|//|\*\*|>>|<<)?=(?!=)", RegexOptions.Compiled, RegexTimeout);

    private static readonly Syntax PySyntax = new(
        "py",
        new Regex(@"\b(?:self|cls)\.([A-Za-z_]\w*)\b(\s*\()?", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(?<![\w.])([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(?<!\b(?:self|cls))\.([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(?<![\w.])([A-Za-z_]\w*)\b(?!\s*\()", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"\G\s*(?:[+\-*/%&|^@]|//|\*\*|>>|<<|:)?=(?!=)", RegexOptions.Compiled, RegexTimeout),
        "__init__",
        new HashSet<string> { "if", "elif", "while", "for", "return", "and", "or", "not", "in", "is", "lambda",
            "with", "assert", "del", "yield", "await", "except", "raise", "print", "def", "class" });

    private sealed record PyScope(int Indent, CodeSymbol Symbol, bool IsClass, string? ClassName);

    public static void AnalyzePython(IReadOnlyList<string> files, CodeGraph graph, CancellationToken token)
    {
        var index = new Index(graph, "py");
        var segments = new List<Segment>();

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var source = ReadFile(file, graph);
            if (LooksMinified(source))
            {
                graph.Notes.Add($"Skipped {Path.GetFileName(file)} (looks generated or minified).");
                continue;
            }
            var lines = CleanPython(source).Split('\n');
            var stack = new List<PyScope>();
            int lastLineNo = 0;     // last line with code on it, i.e. where a closing block ended
            bool decorated = false; // the previous line(s) were @decorators

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(raw)) continue;

                int indent = IndentOf(raw);
                while (stack.Count > 0 && indent <= stack[^1].Indent)
                {
                    CloseScope(stack[^1].Symbol, lastLineNo);
                    stack.RemoveAt(stack.Count - 1);
                }

                var line = raw.TrimStart();
                int lineNo = i + 1;
                lastLineNo = lineNo;
                var parent = stack.Count > 0 ? stack[^1] : null;
                bool wasDecorated = decorated;
                decorated = line.StartsWith('@') || (decorated && line.StartsWith(')')); // multi-line decorator

                var def = PyDef.Match(line);
                if (def.Success)
                {
                    var name = def.Groups[1].Value;
                    CodeSymbol symbol;
                    if (parent is { IsClass: true })
                        symbol = index.Method(file, parent.ClassName!, name, lineNo, isConstructor: name == "__init__");
                    else if (parent != null)
                        symbol = index.Function(file, parent.Symbol.Name, name, lineNo); // nested function
                    else
                        symbol = index.Function(file, null, name, lineNo);

                    // Decorated (@app.route, @pytest.fixture...), dunder (__str__) and test functions
                    // are called by frameworks, not by your code.
                    if (wasDecorated || (name.StartsWith("__") && name.EndsWith("__")) ||
                        name.StartsWith("test_") || name is "setUp" or "tearDown" or "setUpClass" or "tearDownClass")
                        symbol.UsedImplicitly = true;

                    stack.Add(new PyScope(indent, symbol, false, parent?.ClassName));

                    // One-line body: def f(): return g()
                    int colon = line.LastIndexOf(':');
                    if (colon > 0 && colon < line.Length - 1)
                        segments.Add(new Segment(symbol, file, parent?.ClassName, line[(colon + 1)..], null));
                    continue;
                }

                var cls = PyClass.Match(line);
                if (cls.Success)
                {
                    var name = cls.Groups[1].Value;
                    var symbol = index.Class(file, name, lineNo);
                    if (wasDecorated) symbol.UsedImplicitly = true; // e.g. @dataclass
                    stack.Add(new PyScope(indent, symbol, true, name));
                    continue;
                }

                string? skip = null;
                if (parent is null)
                {
                    var assign = PyAssign.Match(line);
                    if (assign.Success)
                    {
                        // Only the first assignment declares the variable; later ones count as writes.
                        var name = assign.Groups[1].Value;
                        if (index.ModuleVariableIn(file, name) is null) skip = name;
                        index.ModuleVariable(file, name, lineNo);
                    }
                    segments.Add(new Segment(null, file, null, line, skip));
                }
                else if (parent.IsClass)
                {
                    var assign = PyAssign.Match(line);
                    if (assign.Success)
                    {
                        skip = assign.Groups[1].Value;
                        index.Field(file, parent.ClassName!, skip, lineNo);
                    }
                    segments.Add(new Segment(parent.Symbol, file, parent.ClassName, line, skip));
                }
                else
                {
                    if (parent.ClassName != null)
                    {
                        foreach (Match m in PySelfAssign.Matches(line))
                            index.Field(file, parent.ClassName, m.Groups[1].Value, lineNo);
                    }
                    segments.Add(new Segment(parent.Symbol, file, parent.ClassName, line, null));
                }
            }

            // End of file closes whatever is still open.
            foreach (var scope in stack) CloseScope(scope.Symbol, lastLineNo);
        }

        ResolveAll(segments, index, PySyntax, graph, token);
    }

    private static void CloseScope(CodeSymbol? symbol, int endLine)
    {
        if (symbol != null) symbol.EndLine = Math.Max(symbol.EndLine, endLine);
    }

    private static int IndentOf(string line)
    {
        int n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>Replace comments and string contents with spaces, keeping line numbers intact.</summary>
    private static string CleanPython(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '#')
            {
                while (i < n && src[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '"' || c == '\'')
            {
                bool triple = i + 2 < n && src[i + 1] == c && src[i + 2] == c;
                int quoteLength = triple ? 3 : 1;
                sb.Append(c, quoteLength);
                i += quoteLength;
                while (i < n)
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        Blank(sb, src[i]);
                        Blank(sb, src[i + 1]);
                        i += 2;
                        continue;
                    }
                    bool closes = triple
                        ? i + 2 < n && src[i] == c && src[i + 1] == c && src[i + 2] == c
                        : src[i] == c;
                    if (closes)
                    {
                        sb.Append(c, quoteLength);
                        i += quoteLength;
                        break;
                    }
                    if (!triple && src[i] == '\n') break; // unterminated string
                    Blank(sb, src[i]);
                    i++;
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // =====================================================================
    // JavaScript / TypeScript
    // =====================================================================

    private const string JsName = @"[A-Za-z_$][\w$]*";
    private const string JsModifiers = @"(?:(?:public|private|protected|static|async|readonly|override|abstract|declare|get|set|accessor)\s+)*";

    private static readonly Regex JsClass = new($@"\bclass\s+({JsName})", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsIgnoredBlock = new($@"\b(?:interface|enum|namespace|module)\s+{JsName}", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsFunction = new($@"\bfunction\s*\*?\s*({JsName})\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsFunctionVar = new(
        $@"\b(?:const|let|var)\s+({JsName})\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|(?:\([^()]*\)|{JsName})\s*(?::[^=]+)?=>)",
        RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsVariable = new($@"^\s*(?:export\s+)?(?:const|let|var)\s+({JsName})", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsMethod = new($@"^\s*{JsModifiers}\*?\s*(#?{JsName})\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsField = new($@"^\s*{JsModifiers}(#?{JsName})\s*[?!]?\s*(?::[^=;]*)?(?:=(?!>)|;|$)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JsThisAssign = new($@"\bthis\.(#?{JsName})\s*(?:[+\-*/%&|^]|\*\*|\?\?|&&|\|\|)?=(?![=>])", RegexOptions.Compiled, RegexTimeout);

    private static readonly Syntax JsSyntax = new(
        "js",
        new Regex($@"\bthis\.(#?{JsName})(?![\w$])(\s*\()?", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<![\w$.#])({JsName})\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<!\bthis)\.(#?{JsName})\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<![\w$.#])({JsName})(?![\w$])(?!\s*\()", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"\G\s*(?:[+\-*/%&|^]|\*\*|\?\?|&&|\|\||>>>|>>|<<)?=(?![=>])", RegexOptions.Compiled, RegexTimeout),
        "constructor",
        new HashSet<string> { "if", "for", "while", "switch", "catch", "function", "return", "typeof", "new",
            "await", "async", "yield", "super", "import", "export", "do", "else", "case", "delete", "void",
            "instanceof", "in", "of", "require", "with" });

    private sealed class JsScope
    {
        public required int Depth;
        public required CodeSymbol? Symbol; // null for ignored blocks (interface / enum)
        public required bool IsClass;
        public required string? ClassName;
        public bool Ignore;
        public bool IsInterface; // members of an interface are called through it, never "unused"
    }

    public static void AnalyzeJavaScript(IReadOnlyList<string> files, CodeGraph graph, CancellationToken token)
    {
        var index = new Index(graph, "js");
        var segments = new List<Segment>();

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var source = ReadFile(file, graph);
            if (LooksMinified(source))
            {
                graph.Notes.Add($"Skipped {Path.GetFileName(file)} (looks minified or bundled).");
                continue;
            }
            var lines = CleanJavaScript(source).Split('\n');
            var stack = new List<JsScope>();
            int depth = 0;
            JsScope? pending = null; // declared, waiting for its opening "{"
            int pendingParens = 0;   // open "(" still to close before the body "{" (parameter lists)
            int lastLineNo = 0;
            bool decorated = false;  // previous line was a TypeScript @Decorator(...)

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                int lineNo = i + 1;
                lastLineNo = lineNo;
                int pendingFrom = 0; // where on this line the pending declaration's text starts
                bool wasDecorated = decorated;
                decorated = line.TrimStart().StartsWith('@');

                var inner = stack.Count > 0 ? stack[^1] : null;
                bool ignored = stack.Any(s => s.Ignore);
                string? className = stack.LastOrDefault(s => s.IsClass)?.ClassName;
                bool inFunction = stack.Any(s => !s.IsClass && !s.Ignore);
                bool atClassBody = inner is { IsClass: true } && depth == inner.Depth;

                CodeSymbol? declared = null;
                bool declaredIsClass = false, declaredIgnore = false;
                int declStart = 0;  // text before this belongs to the current owner...
                int cut = 0;        // ...and text after this belongs to the new declaration
                string? skip = null;

                if (!ignored)
                {
                    Match m;
                    if (atClassBody && (m = JsMethod.Match(line)).Success && !JsSyntax.Keywords.Contains(m.Groups[1].Value))
                    {
                        var name = m.Groups[1].Value;
                        declared = index.Method(file, inner!.ClassName!, name, lineNo, isConstructor: name == "constructor");
                        declStart = m.Index;
                        cut = m.Index + m.Length;
                    }
                    else if (atClassBody && (m = JsField.Match(line)).Success)
                    {
                        var name = m.Groups[1].Value;
                        if (line.IndexOf("=>", m.Index, StringComparison.Ordinal) > 0)
                        {
                            declared = index.Method(file, inner!.ClassName!, name, lineNo, isConstructor: false); // handler = () => {...}
                            declStart = m.Index;
                        cut = m.Index + m.Length;
                        }
                        else
                        {
                            index.Field(file, inner!.ClassName!, name, lineNo);
                            skip = name;
                        }
                    }
                    else if ((m = JsIgnoredBlock.Match(line)).Success)
                    {
                        declaredIgnore = true;
                        declStart = m.Index;
                        cut = m.Index + m.Length;
                    }
                    else if ((m = JsClass.Match(line)).Success)
                    {
                        declared = index.Class(file, m.Groups[1].Value, lineNo);
                        declaredIsClass = true;
                        declStart = m.Index;
                        cut = m.Index + m.Length;
                    }
                    else if ((m = JsFunction.Match(line)).Success || (m = JsFunctionVar.Match(line)).Success)
                    {
                        var container = inFunction ? stack.Last(s => !s.IsClass && !s.Ignore).Symbol?.Name : null;
                        declared = index.Function(file, container, m.Groups[1].Value, lineNo);
                        declStart = m.Index;
                        cut = m.Index + m.Length;
                    }
                    else if (!inFunction && className is null && (m = JsVariable.Match(line)).Success)
                    {
                        skip = m.Groups[1].Value;
                        index.ModuleVariable(file, skip, lineNo);
                    }

                    if (className != null && inFunction)
                    {
                        foreach (Match t in JsThisAssign.Matches(line))
                            index.Field(file, className, t.Groups[1].Value, lineNo);
                    }

                    // Hand the text to whoever owns it.
                    var owner = stack.LastOrDefault(s => !s.Ignore)?.Symbol;
                    var ownerClass = className;
                    if (declared != null && !declaredIsClass)
                    {
                        // The declaration itself ("function foo(") is skipped so it doesn't look like a call.
                        segments.Add(new Segment(owner, file, ownerClass, line[..declStart], skip));
                        segments.Add(new Segment(declared, file, className, line[cut..], null));
                    }
                    else
                    {
                        segments.Add(new Segment(owner, file, ownerClass, line, skip));
                    }
                }

                if (declared != null)
                {
                    // Exported, decorated or framework-called things are used from outside this code.
                    bool exported = declStart > 0 && Regex.IsMatch(line[..declStart], @"\bexport\b");
                    if (exported || wasDecorated || JsFrameworkMethods.Contains(declared.Name) ||
                        Regex.IsMatch(line, @"^\s*(?:static\s+)?(?:get|set)\s"))
                        declared.UsedImplicitly = true;
                }

                if (declared != null || declaredIgnore)
                {
                    pendingFrom = cut;
                    // Method / function patterns end just after "(", so we're inside the parameter list.
                    pendingParens = cut > 0 && line[cut - 1] == '(' ? 1 : 0;
                    pending = new JsScope
                    {
                        Depth = 0,
                        Symbol = declared,
                        IsClass = declaredIsClass,
                        ClassName = declaredIsClass ? declared!.Name : className,
                        Ignore = declaredIgnore,
                    };
                }

                // Track braces to know when each scope starts and ends.
                for (int j = 0; j < line.Length; j++)
                {
                    char c = line[j];
                    if (pending != null && j >= pendingFrom)
                    {
                        if (c == '(') pendingParens++;
                        else if (c == ')') pendingParens--;
                    }

                    if (c == '{')
                    {
                        depth++;
                        // Skip "{" inside a parameter list, e.g. f({ a, b }) or (opts: { x: number })
                        if (pending != null && j >= pendingFrom && pendingParens <= 0)
                        {
                            pending.Depth = depth;
                            stack.Add(pending);
                            pending = null;
                        }
                    }
                    else if (c == '}')
                    {
                        depth = Math.Max(0, depth - 1);
                        while (stack.Count > 0 && stack[^1].Depth > depth)
                        {
                            CloseScope(stack[^1].Symbol, lineNo);
                            stack.RemoveAt(stack.Count - 1);
                        }
                    }
                }

                // A declaration with no body on this line: arrow expression, overload signature or field.
                if (pending != null)
                {
                    var trimmed = line.TrimEnd();
                    bool arrowExpression = false;
                    int arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
                    if (pending.Symbol != null && !pending.IsClass && arrow >= 0)
                    {
                        var afterArrow = line[(arrow + 2)..].Trim();
                        arrowExpression = afterArrow.Length > 0 && !afterArrow.StartsWith('{');
                    }
                    if ((trimmed.EndsWith(';') && pendingParens <= 0) || arrowExpression) pending = null;
                }
            }

            CloseRemaining(stack, lastLineNo); // end of file closes whatever is still open
        }

        ResolveAll(segments, index, JsSyntax, graph, token);
    }

    // Methods that frameworks and the browser call for you.
    private static readonly HashSet<string> JsFrameworkMethods = new()
    {
        "render", "componentDidMount", "componentDidUpdate", "componentWillUnmount", "shouldComponentUpdate",
        "getDerivedStateFromProps", "getSnapshotBeforeUpdate", "componentDidCatch",
        "connectedCallback", "disconnectedCallback", "attributeChangedCallback", "adoptedCallback",
        "ngOnInit", "ngOnDestroy", "ngOnChanges", "ngAfterViewInit", "mounted", "created", "setup",
        "toString", "toJSON", "valueOf", "handleEvent",
    };

    private static void CloseRemaining(List<JsScope> stack, int lastLineNo)
    {
        foreach (var scope in stack) CloseScope(scope.Symbol, lastLineNo);
    }

    // After one of these words, "/" starts a regex (return /x/), not a division.
    private static readonly HashSet<string> RegexKeywords = new()
    {
        "return", "typeof", "case", "do", "else", "in", "of", "void", "yield", "await",
        "delete", "instanceof", "new", "throw",
    };

    /// <summary>
    /// Decide whether a "/" (not "//" or "/*") starts a regex literal or is a division,
    /// by looking at the last meaningful thing before it: a value (name, number, ")" or "]")
    /// means division; an operator, bracket, keyword or start of file means regex.
    /// </summary>
    private static bool IsRegexLiteralStart(StringBuilder cleaned)
    {
        int j = cleaned.Length - 1;
        while (j >= 0 && char.IsWhiteSpace(cleaned[j])) j--;
        if (j < 0) return true;

        char last = cleaned[j];
        if (last == ')' || last == ']' || last == '"' || last == '\'' || last == '`') return false;
        if (char.IsLetterOrDigit(last) || last == '_' || last == '$')
        {
            int end = j;
            while (j >= 0 && (char.IsLetterOrDigit(cleaned[j]) || cleaned[j] == '_' || cleaned[j] == '$')) j--;
            var word = cleaned.ToString(j + 1, end - j);
            return RegexKeywords.Contains(word);
        }
        return true; // ( , = : [ ! & | ? { } ; + - * % < > ~ ^
    }

    /// <summary>Copy a regex literal with its contents blanked. Returns the index just after it.</summary>
    private static int SkipRegexLiteral(string src, int start, StringBuilder sb)
    {
        int n = src.Length;
        int i = start + 1;
        bool inClass = false; // inside [...], a "/" doesn't end the regex
        while (i < n && src[i] != '\n')
        {
            char c = src[i];
            if (c == '\\' && i + 1 < n && src[i + 1] != '\n') { i += 2; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                // Found the end: write "/", blanks for the body, "/".
                sb.Append('/');
                sb.Append(' ', i - start - 1);
                sb.Append('/');
                return i + 1;
            }
            i++;
        }

        // No closing "/" on this line, so it was a division after all. Just copy the "/".
        sb.Append('/');
        return start + 1;
    }

    // =====================================================================
    // Java / Kotlin
    // =====================================================================

    private const string JvmName = @"[A-Za-z_$][\w$]*";
    private const string JvmAnnotations = @"(?:@[\w.]+(?:\([^)]*\))?\s+)*";
    private const string JavaModifiers =
        @"(?:(?:public|private|protected|static|final|abstract|synchronized|native|default|strictfp|transient|volatile|sealed|non-sealed)\s+)*";
    private const string JavaType = @"[\w.$]+(?:<[^()]*?>)?(?:\[\])*"; // int, String, List<Foo>, byte[]

    private static readonly Regex JavaTypeDecl = new($@"\b(class|interface|enum|record)\s+({JvmName})", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JavaCtor = new($@"^\s*{JvmAnnotations}(?:(?:public|private|protected)\s+)?({JvmName})\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JavaMethod = new(
        $@"^\s*{JvmAnnotations}{JavaModifiers}(?:<[^>]+>\s+)?{JavaType}\s+({JvmName})\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex JavaField = new(
        $@"^\s*{JvmAnnotations}{JavaModifiers}{JavaType}\s+({JvmName})\s*(?:=|;|,)", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex KotlinTypeDecl = new($@"\b(class|interface|object)\s+({JvmName})", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex KotlinCompanion = new(@"\bcompanion\s+object\b", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex KotlinFun = new(
        $@"\bfun\s+(?:<[^>]*>\s*)?(?:[\w.<>?]+\.)?({JvmName})\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex KotlinProperty = new(
        $@"^\s*{JvmAnnotations}(?:(?:private|public|protected|internal|override|open|lateinit|const|abstract|final)\s+)*(?:val|var)\s+({JvmName})",
        RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex KotlinHeaderProperty = new($@"\b(?:val|var)\s+({JvmName})", RegexOptions.Compiled, RegexTimeout);

    private static readonly Syntax JvmSyntax = new(
        "jvm",
        new Regex($@"\bthis\.({JvmName})(?![\w$])(\s*\()?", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<![\w$.])({JvmName})\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<!\bthis)\.({JvmName})\s*\(", RegexOptions.Compiled, RegexTimeout),
        new Regex($@"(?<![\w$.])({JvmName})(?![\w$])(?!\s*\()", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"\G\s*(?:[+\-*/%&|^]|<<|>>>|>>)?=(?!=)", RegexOptions.Compiled, RegexTimeout),
        null, // constructors are named after their class
        new HashSet<string> { "if", "for", "while", "switch", "catch", "return", "new", "else", "throw", "super",
            "this", "synchronized", "try", "do", "when", "is", "in", "as", "fun", "val", "var", "class", "object",
            "interface", "import", "package", "assert", "instanceof", "null", "true", "false" },
        ImplicitThis: true);

    // Names frameworks call for you.
    private static readonly HashSet<string> JvmFrameworkMethods = new()
    {
        "main", "toString", "equals", "hashCode", "compareTo", "run", "call", "close", "iterator",
        "onCreate", "onStart", "onResume", "onPause", "onStop", "onDestroy", "onCreateView", "onViewCreated",
        "invoke", "get", "set", "component1", "component2",
    };

    public static void AnalyzeJvm(IReadOnlyList<string> files, CodeGraph graph, CancellationToken token)
    {
        var index = new Index(graph, "jvm");
        var segments = new List<Segment>();

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var source = ReadFile(file, graph);
            if (LooksMinified(source))
            {
                graph.Notes.Add($"Skipped {Path.GetFileName(file)} (looks generated).");
                continue;
            }

            try
            {
                AnalyzeJvmFile(file, source, index, segments);
            }
            catch (RegexMatchTimeoutException)
            {
                graph.Notes.Add($"Skipped part of {Path.GetFileName(file)} (too unusual to read).");
            }
        }

        ResolveAll(segments, index, JvmSyntax, graph, token);
    }

    private static void AnalyzeJvmFile(string file, string source, Index index, List<Segment> segments)
    {
        bool kotlin = Path.GetExtension(file).ToLowerInvariant() is ".kt" or ".kts";
        var lines = CleanCLike(source, kotlin).Split('\n');
        var stack = new List<JsScope>();
        int depth = 0;
        JsScope? pending = null;
        int pendingParens = 0;
        int lastLineNo = 0;
        bool annotated = false; // previous line was only an @Annotation

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            int lineNo = i + 1;
            lastLineNo = lineNo;
            int pendingFrom = 0;
            var trimmed = line.TrimStart();

            bool wasAnnotated = annotated;
            annotated = trimmed.StartsWith('@') && !trimmed.Contains('(') || Regex.IsMatch(trimmed, @"^@[\w.]+\([^)]*\)\s*$");
            bool hasAnnotation = wasAnnotated || trimmed.StartsWith('@');

            var inner = stack.Count > 0 ? stack[^1] : null;
            var classScope = stack.LastOrDefault(s => s.IsClass);
            string? className = classScope?.ClassName;
            bool inFunction = stack.Any(s => !s.IsClass);
            bool atClassBody = inner is { IsClass: true } && depth == inner.Depth;

            CodeSymbol? declared = null;
            bool declaredIsClass = false, declaredIsInterface = false;
            string? declaredClassName = null;
            int declStart = 0, cut = 0;
            string? skip = null;
            Match m;

            if (kotlin)
            {
                if ((m = KotlinCompanion.Match(line)).Success && className != null)
                {
                    // Members of a companion object belong to the enclosing class.
                    declared = index.ClassNamed(className);
                    declaredIsClass = true;
                    declaredClassName = className;
                    declStart = m.Index;
                    cut = m.Index + m.Length;
                }
                else if ((m = KotlinTypeDecl.Match(line)).Success && !inFunction)
                {
                    var name = m.Groups[2].Value;
                    declared = index.Class(file, name, lineNo);
                    declaredIsClass = true;
                    declaredIsInterface = m.Groups[1].Value == "interface";
                    declaredClassName = name;
                    declStart = m.Index;
                    cut = m.Index + m.Length;
                    // Primary constructor properties: class User(val name: String, var age: Int)
                    foreach (Match p in KotlinHeaderProperty.Matches(line, cut))
                        index.Field(file, name, p.Groups[1].Value, lineNo);
                }
                else if ((m = KotlinFun.Match(line)).Success)
                {
                    var name = m.Groups[1].Value;
                    declared = atClassBody && className != null
                        ? index.Method(file, className, name, lineNo, isConstructor: false)
                        : index.Function(file, inFunction ? stack.Last(s => !s.IsClass).Symbol?.Name : null, name, lineNo);
                    declStart = m.Index;
                    cut = m.Index + m.Length;
                    if (Regex.IsMatch(line[..m.Index], @"\b(?:override|abstract)\b")) declared.UsedImplicitly = true;
                }
                else if ((m = KotlinProperty.Match(line)).Success && !inFunction)
                {
                    var name = m.Groups[1].Value;
                    var property = className != null && atClassBody
                        ? index.Field(file, className, name, lineNo)
                        : className == null ? index.ModuleVariable(file, name, lineNo) : null;
                    if (property != null)
                    {
                        skip = name;
                        if (hasAnnotation || Regex.IsMatch(line[..m.Groups[1].Index], @"\boverride\b")) property.UsedImplicitly = true;
                    }
                }
            }
            else
            {
                if ((m = JavaTypeDecl.Match(line)).Success && !inFunction)
                {
                    var name = m.Groups[2].Value;
                    declared = index.Class(file, name, lineNo);
                    declaredIsClass = true;
                    declaredIsInterface = m.Groups[1].Value == "interface";
                    declaredClassName = name;
                    declStart = m.Index;
                    cut = m.Index + m.Length;
                }
                else if (atClassBody && className != null && (m = JavaCtor.Match(line)).Success && m.Groups[1].Value == className)
                {
                    declared = index.Method(file, className, className, lineNo, isConstructor: true);
                    declStart = m.Groups[1].Index;
                    cut = m.Index + m.Length;
                }
                else if (atClassBody && className != null && (m = JavaMethod.Match(line)).Success
                         && !JvmSyntax.Keywords.Contains(m.Groups[1].Value))
                {
                    declared = index.Method(file, className, m.Groups[1].Value, lineNo, isConstructor: false);
                    declStart = m.Groups[1].Index;
                    cut = m.Index + m.Length;
                    if (Regex.IsMatch(line[..m.Groups[1].Index], @"\b(?:abstract|native|default)\b")) declared.UsedImplicitly = true;
                }
                else if (atClassBody && className != null && (m = JavaField.Match(line)).Success
                         && !JvmSyntax.Keywords.Contains(m.Groups[1].Value))
                {
                    var name = m.Groups[1].Value;
                    var field = index.Field(file, className, name, lineNo);
                    if (hasAnnotation) field.UsedImplicitly = true; // @Autowired, @Inject, @Column...
                    skip = name;
                }
            }

            if (declared != null && !declaredIsClass)
            {
                // @Override / @Test / @GetMapping..., interface members and well-known framework
                // methods are called by something the analysis can't see.
                if (hasAnnotation || classScope?.IsInterface == true || JvmFrameworkMethods.Contains(declared.Name))
                    declared.UsedImplicitly = true;
            }
            else if (declared != null && hasAnnotation && declared.Kind == CodeKind.Class)
            {
                declared.UsedImplicitly = true; // @SpringBootApplication, @Entity...
            }

            // Hand the text to whoever owns it (the declaration itself is skipped).
            var owner = stack.LastOrDefault()?.Symbol;
            if (declared != null && !declaredIsClass)
            {
                segments.Add(new Segment(owner, file, className, line[..declStart], skip));
                segments.Add(new Segment(declared, file, className, line[cut..], null));
            }
            else
            {
                segments.Add(new Segment(owner, file, className, line, skip));
            }

            if (declared != null)
            {
                pendingFrom = cut;
                pendingParens = cut > 0 && line[cut - 1] == '(' ? 1 : 0;
                pending = new JsScope
                {
                    Depth = 0,
                    Symbol = declared,
                    IsClass = declaredIsClass,
                    ClassName = declaredIsClass ? declaredClassName : className,
                    IsInterface = declaredIsInterface,
                };
            }

            // Braces open and close scopes; "{" inside a parameter list doesn't count.
            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];
                if (pending != null && j >= pendingFrom)
                {
                    if (c == '(') pendingParens++;
                    else if (c == ')') pendingParens--;
                }
                if (c == '{')
                {
                    depth++;
                    if (pending != null && j >= pendingFrom && pendingParens <= 0)
                    {
                        pending.Depth = depth;
                        stack.Add(pending);
                        pending = null;
                    }
                }
                else if (c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                    while (stack.Count > 0 && stack[^1].Depth > depth)
                    {
                        CloseScope(stack[^1].Symbol, lineNo);
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
            }

            // A declaration with no body on this line.
            if (pending != null && pendingParens <= 0)
            {
                var rest = line[Math.Min(pendingFrom, line.Length)..];
                bool endsStatement = line.TrimEnd().EndsWith(';');                  // abstract / interface method
                // Kotlin puts "{" on the same line, so no "{" means "fun f() = x" or an abstract/interface fun.
                bool expressionBody = !pending.IsClass && kotlin;
                bool bodilessKotlinClass = pending.IsClass && kotlin && !rest.TrimEnd().EndsWith(',') && !rest.TrimEnd().EndsWith(':');
                if (endsStatement || expressionBody || bodilessKotlinClass) pending = null;
            }
        }

        foreach (var scope in stack) CloseScope(scope.Symbol, lastLineNo);
    }

    /// <summary>
    /// Blank out comments and string/char contents for C-style languages (Java, Kotlin), keeping
    /// line numbers. Handles "text", 'c', and triple-quoted text blocks / raw strings.
    /// </summary>
    private static string CleanCLike(string src, bool kotlin)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            char next = i + 1 < n ? src[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && src[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/')) { Blank(sb, src[i]); i++; }
                if (i < n) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')
            {
                // Text block (Java) / raw string (Kotlin): """ ... """
                sb.Append("\"\"\"");
                i += 3;
                while (i < n && !(src[i] == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')) { Blank(sb, src[i]); i++; }
                if (i < n) { sb.Append("\"\"\""); i += 3; }
                continue;
            }
            if (c == '"' || c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        Blank(sb, src[i]);
                        Blank(sb, src[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (src[i] == c)
                    {
                        sb.Append(c);
                        i++;
                        break;
                    }
                    if (src[i] == '\n') break; // unterminated
                    Blank(sb, src[i]);
                    i++;
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Replace comments and string/template contents with spaces, keeping line numbers intact.</summary>
    private static string CleanJavaScript(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            char next = i + 1 < n ? src[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && src[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/')) { Blank(sb, src[i]); i++; }
                if (i < n) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c == '/' && IsRegexLiteralStart(sb))
            {
                i = SkipRegexLiteral(src, i, sb);
                continue;
            }
            if (c == '"' || c == '\'' || c == '`')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        Blank(sb, src[i]);
                        Blank(sb, src[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (src[i] == c)
                    {
                        sb.Append(c);
                        i++;
                        break;
                    }
                    if (c != '`' && src[i] == '\n') break; // unterminated
                    Blank(sb, src[i]);
                    i++;
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
