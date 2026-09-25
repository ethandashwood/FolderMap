using FolderMap.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FolderMap.Services;

/// <summary>
/// Accurate C# analysis using Roslyn (the real C# compiler). Every name in a method body
/// is resolved to the exact method, property or field it refers to, so overloads,
/// partial classes and same-named members in different classes are all told apart.
/// </summary>
internal static class CSharpCodeAnalyzer
{
    private const string Lang = "cs";

    // The .NET runtime's own assemblies, so things like List<T> and string resolve.
    private static readonly Lazy<List<MetadataReference>> References = new(() =>
    {
        var list = new List<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string paths)
        {
            foreach (var path in paths.Split(Path.PathSeparator))
            {
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try { list.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* not a .NET assembly - ignore */ }
            }
        }
        return list;
    });

    public static void Analyze(IReadOnlyList<string> files, CodeGraph graph, IProgress<string>? progress, CancellationToken token)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var text = System.IO.File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: token));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                graph.Notes.Add($"Couldn't read {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var compilation = CSharpCompilation.Create(
            "FolderMapAnalysis", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var state = new State(graph);

        // Pass 1a: classes, structs, records, interfaces, enums.
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot(token).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                state.Declare(model.GetDeclaredSymbol(decl, token), decl);
        }

        // Pass 1b: members (needs the classes to exist so members can be linked to them).
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var member in Members(tree.GetRoot(token)))
            {
                if (member is BaseFieldDeclarationSyntax field)
                {
                    foreach (var variable in field.Declaration.Variables)
                        state.Declare(model.GetDeclaredSymbol(variable, token), variable);
                }
                else
                {
                    state.Declare(model.GetDeclaredSymbol(member, token), member);
                }
            }
        }

        // Pass 2: walk every member body and record what it calls, creates, reads and writes.
        int done = 0;
        foreach (var tree in trees)
        {
            token.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(token);

            foreach (var member in Members(root))
            {
                if (member is BaseFieldDeclarationSyntax field)
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (variable.Initializer is null) continue;
                        var from = state.Resolve(model.GetDeclaredSymbol(variable, token));
                        if (from != null) state.Walk(model, from, variable.Initializer, token);
                    }
                }
                else
                {
                    var from = state.Resolve(model.GetDeclaredSymbol(member, token));
                    if (from != null) state.Walk(model, from, member, token);
                }
            }

            // Top-level statements (Program.cs without a Main method).
            var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count > 0)
            {
                var from = state.TopLevel(tree.FilePath, globals[0]);
                foreach (var statement in globals) state.Walk(model, from, statement, token);
            }

            done++;
            if (done % 20 == 0) progress?.Report($"Linking C# code… {done:N0} of {trees.Count:N0} files");
        }
    }

    /// <summary>Member declarations we care about (not types, namespaces or enum values).</summary>
    private static IEnumerable<MemberDeclarationSyntax> Members(SyntaxNode root) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => m is
            BaseMethodDeclarationSyntax or        // methods, constructors, operators, destructors
            BasePropertyDeclarationSyntax or      // properties, indexers, events with accessors
            BaseFieldDeclarationSyntax);          // fields, field-like events

    private sealed class State
    {
        private readonly CodeGraph _graph;
        private readonly Dictionary<ISymbol, CodeSymbol> _map = new(SymbolEqualityComparer.Default);

        public State(CodeGraph graph) => _graph = graph;

        private static ISymbol Normalize(ISymbol symbol)
        {
            symbol = symbol.OriginalDefinition;
            if (symbol is IMethodSymbol m)
            {
                if (m.ReducedFrom != null) symbol = m.ReducedFrom;          // extension method called as x.Foo()
                if (m.PartialDefinitionPart != null) symbol = m.PartialDefinitionPart;
            }
            return symbol;
        }

        public void Declare(ISymbol? symbol, SyntaxNode declaration)
        {
            if (symbol is null) return;
            symbol = Normalize(symbol);
            if (_map.ContainsKey(symbol)) return;

            CodeKind? kind = symbol switch
            {
                INamedTypeSymbol t when t.TypeKind != TypeKind.Delegate => CodeKind.Class,
                IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => CodeKind.Constructor,
                IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation
                    or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.Destructor } => CodeKind.Method,
                IPropertySymbol => CodeKind.Property,
                IFieldSymbol f when f.ContainingType?.TypeKind != TypeKind.Enum => CodeKind.Field,
                _ => null,
            };
            if (kind is null) return;

            var span = declaration.GetLocation().GetLineSpan();
            string name = kind == CodeKind.Constructor ? symbol.ContainingType.Name : symbol.Name;
            string? container = symbol is INamedTypeSymbol type
                ? type.ContainingType?.Name
                : symbol.ContainingType?.Name;

            var id = $"{Lang}:{kind}:{symbol.ToDisplayString()}";
            var node = _graph.GetOrAdd(id, () => new CodeSymbol(
                id, name, kind.Value, container, declaration.SyntaxTree.FilePath,
                span.StartLinePosition.Line + 1, Lang));
            _map[symbol] = node;

            // Where the body ends, for the code preview.
            if (declaration.SyntaxTree.FilePath == node.File)
                node.EndLine = Math.Max(node.EndLine, span.EndLinePosition.Line + 1);

            if (IsUsedImplicitly(symbol, declaration)) node.UsedImplicitly = true;

            if (symbol is not INamedTypeSymbol && symbol.ContainingType != null && Resolve(symbol.ContainingType) is { } owner)
                _graph.Link(owner, node, LinkKind.Contains);
        }

        /// <summary>
        /// Things that are called or read by something the analysis can't see - a framework,
        /// a base class, an interface, or code generated from an attribute - so they must never
        /// be reported as unused.
        /// </summary>
        private static bool IsUsedImplicitly(ISymbol symbol, SyntaxNode declaration)
        {
            // [Fact], [RelayCommand], [ObservableProperty], [HttpGet], [JsonPropertyName]...
            if (symbol.GetAttributes().Length > 0) return true;
            if (symbol.IsOverride || symbol.IsVirtual || symbol.IsAbstract) return true;

            if (symbol is IMethodSymbol method)
            {
                if (method.Name == "Main" && method.IsStatic) return true;
                if (method.MethodKind is MethodKind.ExplicitInterfaceImplementation or MethodKind.UserDefinedOperator
                    or MethodKind.Conversion or MethodKind.Destructor) return true;
            }

            // Partial methods are usually hooks filled in by code generators (e.g. OnNameChanged).
            if (declaration is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.PartialKeyword))
                return true;

            // Implements an interface member, so it's called through the interface.
            var type = symbol.ContainingType;
            if (type != null && symbol is not INamedTypeSymbol)
            {
                foreach (var iface in type.AllInterfaces)
                    foreach (var interfaceMember in iface.GetMembers())
                        if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(interfaceMember), symbol))
                            return true;
            }
            return false;
        }

        public CodeSymbol? Resolve(ISymbol? symbol)
        {
            if (symbol is null) return null;
            symbol = Normalize(symbol);

            // Property getters/setters count as the property itself.
            if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol property })
                symbol = Normalize(property);

            return _map.TryGetValue(symbol, out var node) ? node : null;
        }

        public CodeSymbol TopLevel(string file, SyntaxNode first)
        {
            var id = $"{Lang}:toplevel:{file}";
            int line = first.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return _graph.GetOrAdd(id, () => new CodeSymbol(
                id, "(top-level code)", CodeKind.Function, Path.GetFileNameWithoutExtension(file), file, line, Lang));
        }

        public void Walk(SemanticModel model, CodeSymbol from, SyntaxNode scope, CancellationToken token)
        {
            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case BaseObjectCreationExpressionSyntax creation: // new Foo(...) and new(...)
                    {
                        if (model.GetSymbolInfo(creation, token).Symbol is IMethodSymbol ctor)
                        {
                            var target = Resolve(ctor) ?? Resolve(ctor.ContainingType);
                            if (target != null) _graph.Link(from, target, LinkKind.Creates);
                        }
                        break;
                    }
                    case ConstructorInitializerSyntax initializer: // : base(...) / : this(...)
                    {
                        var target = Resolve(model.GetSymbolInfo(initializer, token).Symbol);
                        if (target != null) _graph.Link(from, target, LinkKind.Calls);
                        break;
                    }
                    case SimpleNameSyntax name:
                        HandleName(model, from, name, token);
                        break;
                }
            }
        }

        private void HandleName(SemanticModel model, CodeSymbol from, SimpleNameSyntax name, CancellationToken token)
        {
            var info = model.GetSymbolInfo(name, token);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);

            switch (symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor
                    when accessor.AssociatedSymbol is IPropertySymbol:
                {
                    var target = Resolve(accessor);
                    if (target != null) _graph.Link(from, target, IsWrite(name) ? LinkKind.Writes : LinkKind.Reads);
                    break;
                }
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }:
                    break; // part of the method it's written in
                case IMethodSymbol method:
                {
                    var target = Resolve(method);
                    if (target != null) _graph.Link(from, target, LinkKind.Calls);
                    break;
                }
                case IPropertySymbol or IFieldSymbol:
                {
                    var target = Resolve(symbol);
                    if (target != null) _graph.Link(from, target, IsWrite(name) ? LinkKind.Writes : LinkKind.Reads);
                    break;
                }
            }
        }

        /// <summary>Is this name being assigned to (x = 1, x += 1, x++, out x) rather than read?</summary>
        private static bool IsWrite(SimpleNameSyntax name)
        {
            ExpressionSyntax expression = name;
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name) expression = access;
            else if (name.Parent is MemberBindingExpressionSyntax binding && binding.Name == name) expression = binding;

            return expression.Parent switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left == expression,
                PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression)
                                                      || prefix.IsKind(SyntaxKind.PreDecrementExpression),
                PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression)
                                                        || postfix.IsKind(SyntaxKind.PostDecrementExpression),
                ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                           || argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
                _ => false,
            };
        }
    }
}
