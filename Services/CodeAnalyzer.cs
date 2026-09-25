using System.Text.RegularExpressions;
using FolderMap.Models;

namespace FolderMap.Services;

public enum CodeLanguage { CSharp, Python, JavaScript, Java }

/// <summary>Finds code files and hands them to the right language analyser.</summary>
public static class CodeAnalyzer
{
    public const int MaxFiles = 3000;
    private const long MaxFileBytes = 1_000_000; // bigger files are usually generated or minified

    // Folders that hold dependencies, build output or tooling rather than your own code.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".vscode", ".idea", "packages",
        "venv", ".venv", "env", "__pycache__", "site-packages", ".mypy_cache", ".pytest_cache",
        "dist", "build", ".next", ".nuxt", "coverage", ".turbo", ".cache",
        "target", ".gradle", ".kotlin", ".mvn",   // Maven / Gradle output and tooling
    };

    public static CodeLanguage? LanguageOf(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.EndsWith(".min.js") || name.EndsWith(".d.ts")) return null;
        return Path.GetExtension(name) switch
        {
            ".cs" => CodeLanguage.CSharp,
            ".py" or ".pyw" => CodeLanguage.Python,
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".mts" or ".cts" => CodeLanguage.JavaScript,
            ".java" or ".kt" or ".kts" => CodeLanguage.Java, // Java and Kotlin share one analyser
            _ => null,
        };
    }

    public static bool IsCodeFile(string fileName) => LanguageOf(fileName) != null;

    /// <summary>UI files that can call code by name (XAML event handlers and bindings, HTML onclick...).</summary>
    public static bool IsMarkupFile(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is
        ".xaml" or ".axaml" or ".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte";

    public static IEnumerable<FileNode> MarkupFilesIn(FolderNode folder)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            foreach (var file in f.Files)
                if (file.Size <= MaxFileBytes && !file.IsCloudOnly && IsMarkupFile(file.Name))
                    yield return file;
            foreach (var sub in f.Folders)
                if (!SkippedFolders.Contains(sub.Name))
                    stack.Push(sub);
        }
    }

    // Attribute values (Click="OnSave", {Binding Total}, onclick="save()") and {{ template }} text.
    private static readonly Regex MarkupValue = new(@"=\s*""([^""]*)""|\{\{([^}]*)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static void ReadMarkupNames(IEnumerable<string> markupFiles, CodeGraph graph, CancellationToken token)
    {
        foreach (var path in markupFiles)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var text = File.ReadAllText(path);
                foreach (Match value in MarkupValue.Matches(text))
                    foreach (Match word in Word.Matches(value.Value))
                        graph.MarkupNames.Add(word.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                // Can't read it - some handlers may then be listed as unused, which is harmless.
            }
        }
    }

    /// <summary>Code files under a scanned folder, skipping dependency and build folders.</summary>
    public static IEnumerable<FileNode> CodeFilesIn(FolderNode folder)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            foreach (var file in f.Files)
                if (file.Size <= MaxFileBytes && !file.IsCloudOnly && IsCodeFile(file.Name))
                    yield return file;
            foreach (var sub in f.Folders)
                if (!SkippedFolders.Contains(sub.Name))
                    stack.Push(sub);
        }
    }

    /// <summary>Quick yes/no: is there at least one code file under this folder? Can be cancelled.</summary>
    public static bool ContainsCode(FolderNode folder, CancellationToken token)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var f = stack.Pop();
            foreach (var file in f.Files)
                if (file.Size <= MaxFileBytes && !file.IsCloudOnly && IsCodeFile(file.Name))
                    return true;
            foreach (var sub in f.Folders)
                if (!SkippedFolders.Contains(sub.Name))
                    stack.Push(sub);
        }
        return false;
    }

    public static CodeGraph Analyze(IReadOnlyList<string> files, IProgress<string>? progress, CancellationToken token,
        IReadOnlyList<string>? markupFiles = null)
    {
        var graph = new CodeGraph();
        if (markupFiles is { Count: > 0 })
        {
            progress?.Report($"Reading {markupFiles.Count:N0} XAML/HTML files…");
            ReadMarkupNames(markupFiles, graph, token);
        }
        var chosen = files.Take(MaxFiles).ToList();
        if (files.Count > MaxFiles)
            graph.Notes.Add($"Only the first {MaxFiles:N0} of {files.Count:N0} code files were analysed.");
        graph.FileCount = chosen.Count;

        var byLanguage = chosen
            .GroupBy(f => LanguageOf(f))
            .Where(g => g.Key != null)
            .ToDictionary(g => g.Key!.Value, g => g.ToList());

        if (byLanguage.TryGetValue(CodeLanguage.CSharp, out var cs))
        {
            progress?.Report($"Reading {cs.Count:N0} C# files…");
            CSharpCodeAnalyzer.Analyze(cs, graph, progress, token);
        }
        if (byLanguage.TryGetValue(CodeLanguage.Python, out var py))
        {
            progress?.Report($"Reading {py.Count:N0} Python files…");
            PatternCodeAnalyzer.AnalyzePython(py, graph, token);
        }
        if (byLanguage.TryGetValue(CodeLanguage.Java, out var jvm))
        {
            progress?.Report($"Reading {jvm.Count:N0} Java/Kotlin files…");
            PatternCodeAnalyzer.AnalyzeJvm(jvm, graph, token);
        }
        if (byLanguage.TryGetValue(CodeLanguage.JavaScript, out var js))
        {
            progress?.Report($"Reading {js.Count:N0} JavaScript/TypeScript files…");
            PatternCodeAnalyzer.AnalyzeJavaScript(js, graph, token);
        }

        graph.Finish();
        return graph;
    }
}
