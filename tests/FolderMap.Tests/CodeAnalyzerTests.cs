using System.Diagnostics;
using FolderMap.Models;
using FolderMap.Services;
using FolderMap.ViewModels;

namespace FolderMap.Tests;

public class CodeAnalyzerTests
{
    internal static CodeGraph Analyze(TempFolder t)
    {
        var root = Scanner.Scan(t.Path, null, CancellationToken.None);
        var files = CodeAnalyzer.CodeFilesIn(root).Select(f => f.FullPath).ToList();
        return CodeAnalyzer.Analyze(files, null, CancellationToken.None);
    }

    internal static bool HasLink(CodeGraph g, string from, string to, LinkKind kind) =>
        g.Links.Any(l => l.From.Name == from && l.To.Name == to && l.Kind == kind);

    internal static void AssertLink(CodeGraph g, string from, string to, LinkKind kind) =>
        Assert.True(HasLink(g, from, to, kind),
            $"Expected '{from}' {kind} '{to}'. Found links:\n" +
            string.Join("\n", g.Links.Where(l => l.Kind != LinkKind.Contains)
                .Select(l => $"  {l.From.DisplayName} {l.Kind} {l.To.DisplayName}")));

    // ---------------------------------------------------------------- C#

    private const string CSharpSource = """
        namespace Demo;

        public class Account
        {
            private decimal _balance;
            public string Owner { get; set; } = "";

            public Account(string owner) { Owner = owner; }

            public void Deposit(decimal amount)
            {
                Validate(amount);
                _balance += amount;
                Log();
            }

            public void Deposit(int cents) { Deposit(cents / 100m); }

            private void Validate(decimal amount)
            {
                if (amount <= 0) throw new System.ArgumentException("amount");
            }

            private void Log() => System.Console.WriteLine(Owner + _balance);
        }

        public static class Bank
        {
            public static Account Open(string name)
            {
                var account = new Account(name);
                account.Deposit(10m);
                return account;
            }
        }
        """;

    [Fact]
    public void CSharp_calls_reads_writes_and_creates_are_found()
    {
        using var t = new TempFolder();
        t.Text("Bank.cs", CSharpSource);

        var g = Analyze(t);

        AssertLink(g, "Deposit", "Validate", LinkKind.Calls);
        AssertLink(g, "Deposit", "_balance", LinkKind.Writes);
        AssertLink(g, "Deposit", "Log", LinkKind.Calls);
        AssertLink(g, "Log", "Owner", LinkKind.Reads);
        AssertLink(g, "Log", "_balance", LinkKind.Reads);
        AssertLink(g, "Account", "Owner", LinkKind.Writes);   // constructor
        AssertLink(g, "Open", "Account", LinkKind.Creates);
        AssertLink(g, "Open", "Deposit", LinkKind.Calls);
    }

    [Fact]
    public void CSharp_overloads_are_separate_and_linked()
    {
        using var t = new TempFolder();
        t.Text("Bank.cs", CSharpSource);

        var g = Analyze(t);
        var deposits = g.Symbols.Where(s => s.Name == "Deposit").ToList();

        Assert.Equal(2, deposits.Count);
        Assert.Contains(g.Links, l => l.From.Name == "Deposit" && l.To.Name == "Deposit" && l.Kind == LinkKind.Calls);
    }

    [Fact]
    public void CSharp_members_belong_to_their_class()
    {
        using var t = new TempFolder();
        t.Text("Bank.cs", CSharpSource);

        var g = Analyze(t);

        AssertLink(g, "Account", "Validate", LinkKind.Contains);
        Assert.Equal("Account", g.Symbols.Single(s => s.Name == "Validate").Container);
    }

    [Fact]
    public void CSharp_partial_classes_across_files_are_one_class()
    {
        using var t = new TempFolder();
        t.Text("A.cs", "partial class Shop { void Buy() { Pay(); } }");
        t.Text("B.cs", "partial class Shop { void Pay() { } }");

        var g = Analyze(t);

        Assert.Single(g.Symbols, s => s.Name == "Shop" && s.Kind == CodeKind.Class);
        AssertLink(g, "Buy", "Pay", LinkKind.Calls);
    }

    [Fact]
    public void CSharp_code_that_does_not_compile_is_still_analysed()
    {
        using var t = new TempFolder();
        t.Text("Broken.cs", "class Half { void A() { B(); UnknownThing.Go( } void B() { } ");

        var g = Analyze(t);

        AssertLink(g, "A", "B", LinkKind.Calls);
    }

    // ---------------------------------------------------------------- Python

    private const string PythonSource = """
        TAX_RATE = 0.2

        class Cart:
            def __init__(self):
                self.items = []

            def add(self, item):
                self.items.append(item)
                self._log("added")

            def total(self):
                # comments like add() must be ignored
                note = "strings like total() too"
                return sum(i.price for i in self.items) * (1 + TAX_RATE)

            def _log(self, msg):
                print(msg)

        def checkout():
            cart = Cart()
            cart.add("apple")
            return cart.total()
        """;

    [Fact]
    public void Python_methods_fields_and_module_variables_are_linked()
    {
        using var t = new TempFolder();
        t.Text("cart.py", PythonSource);

        var g = Analyze(t);

        AssertLink(g, "__init__", "items", LinkKind.Writes);
        AssertLink(g, "add", "items", LinkKind.Reads);
        AssertLink(g, "add", "_log", LinkKind.Calls);
        AssertLink(g, "total", "TAX_RATE", LinkKind.Reads);
        AssertLink(g, "checkout", "__init__", LinkKind.Creates);
        AssertLink(g, "checkout", "add", LinkKind.Calls);
        AssertLink(g, "checkout", "total", LinkKind.Calls);
    }

    [Fact]
    public void Python_comments_and_strings_are_not_mistaken_for_calls()
    {
        using var t = new TempFolder();
        t.Text("cart.py", PythonSource);

        var g = Analyze(t);

        Assert.False(HasLink(g, "total", "add", LinkKind.Calls));
        Assert.False(HasLink(g, "total", "total", LinkKind.Calls));
    }

    // ---------------------------------------------------------------- JS / TS

    private const string TypeScriptSource = """
        const MAX_ITEMS = 10;

        export class Store {
          private items: string[] = [];

          add(item: string): void {
            if (this.items.length >= MAX_ITEMS) return;
            this.items.push(item);
            this.notify();
          }

          notify() {
            render(this.items);
          }
        }

        function render(list: string[]) {
          console.log(list.join(", "));
        }

        function configure(opts: { debug: boolean }) {
          render([]);
        }

        const setup = (opts: { debug: boolean }) => {
          const s = new Store();
          s.add("x");
          configure(opts);
        };
        """;

    [Fact]
    public void TypeScript_classes_functions_and_arrows_are_linked()
    {
        using var t = new TempFolder();
        t.Text("store.ts", TypeScriptSource);

        var g = Analyze(t);

        AssertLink(g, "add", "items", LinkKind.Reads);
        AssertLink(g, "add", "MAX_ITEMS", LinkKind.Reads);
        AssertLink(g, "add", "notify", LinkKind.Calls);
        AssertLink(g, "notify", "render", LinkKind.Calls);
        AssertLink(g, "setup", "Store", LinkKind.Creates);
        AssertLink(g, "setup", "add", LinkKind.Calls);
        AssertLink(g, "setup", "configure", LinkKind.Calls);
    }

    [Fact]
    public void TypeScript_object_types_in_parameters_do_not_confuse_the_parser()
    {
        using var t = new TempFolder();
        t.Text("store.ts", TypeScriptSource);

        var g = Analyze(t);

        // configure(opts: { debug: boolean }) { render([]); } - the "{" in the type isn't the body
        AssertLink(g, "configure", "render", LinkKind.Calls);
    }

    // ---------------------------------------------------------------- files & robustness

    [Fact]
    public void Dependency_and_build_folders_are_skipped()
    {
        using var t = new TempFolder();
        t.Text("src/app.py", "def main(): pass");
        t.Text("node_modules/lib/index.js", "function x() {}");
        t.Text("bin/Debug/Gen.cs", "class Gen {}");
        t.Text(".venv/lib/site.py", "def y(): pass");
        t.Text("dist/bundle.min.js", "function z(){}");

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);
        var files = CodeAnalyzer.CodeFilesIn(root).Select(f => f.Name).ToList();

        Assert.Equal(new[] { "app.py" }, files);
    }

    [Fact]
    public void Empty_and_binary_looking_files_do_not_crash()
    {
        using var t = new TempFolder();
        t.Text("empty.py", "");
        t.Text("empty.ts", "");
        t.File("weird.js", Enumerable.Range(0, 5000).Select(i => (byte)(i % 256)).ToArray());
        t.Text("unclosed.py", "def f(:\n  s = '''never closed\n");
        t.Text("unclosed.js", "function f() { const s = `never closed");

        var g = Analyze(t);

        Assert.NotNull(g);
    }

    [Fact]
    public void A_huge_single_line_file_finishes_in_reasonable_time()
    {
        using var t = new TempFolder();
        var line = "function f(){" + string.Concat(Enumerable.Repeat("g(a.b(c));x=y;", 60_000)) + "}\nfunction g(){}";
        t.Text("bundle.js", line); // ~850 KB on one line, like a minified bundle

        var timer = Stopwatch.StartNew();
        Analyze(t);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Took {timer.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void Analysis_can_be_cancelled()
    {
        using var t = new TempFolder();
        t.Text("a.py", "def f(): pass");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CodeAnalyzer.Analyze(new[] { t.Combine("a.py") }, null, cts.Token));
    }

    // ---------------------------------------------------------------- where bodies end (code preview)

    [Fact]
    public void Method_bodies_know_where_they_end_in_every_language()
    {
        using var t = new TempFolder();
        t.Text("Bank.cs", CSharpSource);
        t.Text("cart.py", PythonSource);
        t.Text("store.ts", TypeScriptSource);

        var g = Analyze(t);

        var csValidate = g.Symbols.Single(s => s.Name == "Validate");
        Assert.Equal(csValidate.Line + 3, csValidate.EndLine);   // 4-line method
        var pyAdd = g.Symbols.Single(s => s.Name == "add" && s.Language == "py");
        Assert.Equal(pyAdd.Line + 2, pyAdd.EndLine);             // def + 2 body lines
        var tsAdd = g.Symbols.Single(s => s.Name == "add" && s.Language == "js");
        Assert.Equal(tsAdd.Line + 4, tsAdd.EndLine);             // add(...) { 3 lines }
        var field = g.Symbols.Single(s => s.Name == "_balance");
        Assert.Equal(field.Line, field.EndLine);
    }

    [Fact]
    public void Preview_shows_numbered_lines_without_common_indentation()
    {
        var lines = new[] { "class A", "{", "    void F()", "    {", "        G();", "    }", "}" };

        var text = CodeMapViewModel.FormatPreview(lines, 3, 6, out var range);

        Assert.Equal("lines 3–6", range);
        var shown = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("3  void F()", shown[0]);
        Assert.Equal("5      G();", shown[2]);
        Assert.Equal(4, shown.Length);
    }

    [Fact]
    public void Preview_of_a_very_long_method_is_cut_short()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => $"x{i}();").ToArray();

        var text = CodeMapViewModel.FormatPreview(lines, 1, 1000, out _);

        Assert.Contains("more lines", text);
        Assert.DoesNotContain("x999()", text);
    }

    [Fact]
    public async Task Selecting_something_in_the_code_map_loads_its_code()
    {
        using var t = new TempFolder();
        var file = t.Text("Bank.cs", CSharpSource);
        var vm = new CodeMapViewModel(t.Path, new[] { file }, null);
        await vm.LoadAsync();

        vm.Selected = vm.ListItems.Single(s => s.Name == "Validate");
        await vm.PreviewTask;

        Assert.Contains("throw new System.ArgumentException", vm.CodePreview);
        Assert.Contains("Bank.cs", vm.PreviewHeader);
    }

    // ---------------------------------------------------------------- unused code

    [Fact]
    public void Python_unused_functions_and_write_only_variables_are_flagged()
    {
        using var t = new TempFolder();
        t.Text("app.py", """
            import flask
            app = flask.Flask(__name__)
            DEBUG = True

            def used():
                return 1

            def forgotten():
                return 2

            @app.route("/")
            def home():
                return used()

            class Thing:
                def __str__(self):
                    return "thing"

                def old_method(self):
                    self.cache = None

            def test_used():
                assert used() == 1

            if __name__ == "__main__":
                DEBUG = False
                home()
            """);

        var g = Analyze(t);
        string? Why(string name) => g.Symbols.Single(s => s.Name == name).UnusedReason;

        Assert.Equal("nothing calls it", Why("forgotten"));
        Assert.Equal("nothing calls it", Why("old_method"));
        Assert.Equal("only ever set, never read", Why("cache"));
        Assert.Equal("only ever set, never read", Why("DEBUG"));
        Assert.Null(Why("used"));
        Assert.Null(Why("home"));      // decorated: the framework calls it
        Assert.Null(Why("__str__"));   // called by Python itself
        Assert.Null(Why("test_used")); // called by pytest
    }

    [Fact]
    public void CSharp_framework_and_interface_members_are_not_flagged_as_unused()
    {
        using var t = new TempFolder();
        var code = t.Text("Shapes.cs", """
            using System;
            public interface IShape { double Area(); }
            public abstract class Base { public abstract void Draw(); }
            public class Square : Base, IShape
            {
                private int _unusedCounter;
                private int _written;
                public double Area() => 4;
                public override void Draw() { _written = 1; }
                [Obsolete] public void Legacy() { }
                private void Helper() { }
                private void OnSaveClick(object sender, EventArgs e) { }
            }
            """);
        var xaml = t.Text("Shapes.axaml", "<Button Click=\"OnSaveClick\" />");

        var g = CodeAnalyzer.Analyze(new[] { code }, null, CancellationToken.None, new[] { xaml });
        // IShape and Base also declare Area/Draw, so look at Square's members specifically.
        string? Why(string name) => g.Symbols.Single(s => s.Name == name && s.Container == "Square").UnusedReason;

        Assert.Equal("nothing calls it", Why("Helper"));
        Assert.Equal("never used", Why("_unusedCounter"));
        Assert.Equal("only ever set, never read", Why("_written"));
        Assert.Null(Why("Area"));        // interface implementation
        Assert.Null(Why("Draw"));        // override
        Assert.Null(Why("Legacy"));      // has an attribute
        Assert.Null(Why("OnSaveClick")); // used from XAML
    }

    [Fact]
    public void JavaScript_exports_and_callbacks_are_not_flagged_as_unused()
    {
        using var t = new TempFolder();
        t.Text("util.js", """
            function format(x) { return "#" + x; }
            function neverCalled() { return 0; }
            export function list(items) { return items.map(format); }
            """);

        var g = Analyze(t);
        string? Why(string name) => g.Symbols.Single(s => s.Name == name).UnusedReason;

        Assert.Equal("nothing calls it", Why("neverCalled"));
        Assert.Null(Why("format")); // passed to map() as a callback
        Assert.Null(Why("list"));   // exported for other files
    }

    [Fact]
    public async Task The_unused_list_only_shows_possibly_unused_items()
    {
        using var t = new TempFolder();
        var file = t.Text("app.py", "def a():\n    b()\n\ndef b():\n    pass\n\ndef c():\n    pass\n\na()\n");
        var vm = new CodeMapViewModel(t.Path, new[] { file }, null);
        await vm.LoadAsync();

        vm.ListMode = 1;

        Assert.Equal(new[] { "c" }, vm.ListItems.Select(s => s.Name));
        Assert.Contains("(1)", vm.UnusedLabel);
    }

    // ---------------------------------------------------------------- call paths

    [Fact]
    public void The_shortest_call_path_is_found()
    {
        using var t = new TempFolder();
        t.Text("flow.py", """
            def click():
                save()

            def save():
                validate()
                write()

            def validate():
                pass

            def write():
                open_file()

            def open_file():
                pass
            """);

        var g = Analyze(t);
        CodeSymbol S(string name) => g.Symbols.Single(s => s.Name == name);

        var path = CodeGraph.FindPath(S("click"), S("open_file"));

        Assert.NotNull(path);
        Assert.Equal(new[] { "save", "write", "open_file" }, path!.Select(l => l.To.Name));
        Assert.Null(CodeGraph.FindPath(S("open_file"), S("click"))); // calls only go one way
        Assert.Null(CodeGraph.FindPath(S("validate"), S("write")));  // siblings aren't connected
    }

    [Fact]
    public async Task The_code_map_shows_a_path_and_can_clear_it()
    {
        using var t = new TempFolder();
        var file = t.Text("flow.py", "def a():\n    b()\n\ndef b():\n    c()\n\ndef c():\n    pass\n");
        var vm = new CodeMapViewModel(t.Path, new[] { file }, null);
        await vm.LoadAsync();
        CodeSymbol S(string name) => vm.ListItems.Single(s => s.Name == name);

        vm.Selected = S("a");
        vm.StartPath();
        vm.Selected = S("c");
        vm.FindPathToSelected();

        Assert.Equal(2, vm.PathSteps.Count);
        Assert.Equal(3, vm.View!.Nodes.Count);
        Assert.Contains("→", vm.ViewNote);

        // Backwards works too, with an explanation.
        vm.Selected = S("c");
        vm.StartPath();
        vm.Selected = S("a");
        vm.FindPathToSelected();
        Assert.Equal(2, vm.PathSteps.Count);
        Assert.Contains("other way round", vm.PathSummary);

        vm.ClearPath();
        Assert.False(vm.HasPathStart);
        Assert.Empty(vm.PathSteps);
    }

    // ---------------------------------------------------------------- Java

    private const string JavaSource = """
        package shop;

        import java.util.List;

        public class Cart {
            private final List<String> items = new ArrayList<>();
            private int count;
            private String unusedNote;

            public Cart() {
                reset();
            }

            public void add(String item) {
                items.add(item);
                count++;
                log("added " + item);
            }

            private void reset() {
                count = 0;
            }

            private void log(String message) {
                System.out.println(message);
            }

            private void forgotten() {
            }

            @Override
            public String toString() {
                return "Cart of " + count;
            }
        }

        class Shop {
            public static void main(String[] args) {
                Cart cart = new Cart();
                cart.add("apple");
            }
        }
        """;

    [Fact]
    public void Java_methods_fields_and_constructors_are_linked()
    {
        using var t = new TempFolder();
        t.Text("shop/Cart.java", JavaSource);

        var g = Analyze(t);

        AssertLink(g, "add", "items", LinkKind.Reads);      // bare field name = this.items
        AssertLink(g, "add", "count", LinkKind.Writes);     // count++
        AssertLink(g, "add", "log", LinkKind.Calls);        // bare method name = this.log()
        AssertLink(g, "Cart", "reset", LinkKind.Calls);     // constructor
        AssertLink(g, "reset", "count", LinkKind.Writes);
        AssertLink(g, "toString", "count", LinkKind.Reads);
        AssertLink(g, "main", "Cart", LinkKind.Creates);    // new Cart()
        AssertLink(g, "main", "add", LinkKind.Calls);
        Assert.Equal(CodeKind.Constructor, g.Symbols.Single(s => s.Name == "Cart" && s.Kind != CodeKind.Class).Kind);
    }

    [Fact]
    public void Java_unused_code_and_end_lines()
    {
        using var t = new TempFolder();
        t.Text("shop/Cart.java", JavaSource);

        var g = Analyze(t);
        string? Why(string name) => g.Symbols.Single(s => s.Name == name).UnusedReason;

        Assert.Equal("nothing calls it", Why("forgotten"));
        Assert.Equal("never used", Why("unusedNote"));
        Assert.Null(Why("toString")); // @Override
        Assert.Null(Why("main"));     // program entry point
        Assert.Null(Why("reset"));
        var add = g.Symbols.Single(s => s.Name == "add");
        Assert.Equal(add.Line + 4, add.EndLine);
    }

    [Fact]
    public void Java_interface_methods_and_overrides_are_not_flagged()
    {
        using var t = new TempFolder();
        t.Text("Shapes.java", """
            interface Shape {
                double area();
            }

            class Circle implements Shape {
                private final double r;

                Circle(double r) {
                    this.r = r;
                }

                @Override
                public double area() {
                    return Math.PI * r * r;
                }
            }
            """);

        var g = Analyze(t);

        Assert.All(g.Symbols.Where(s => s.Name == "area"), s => Assert.Null(s.UnusedReason));
        AssertLink(g, "Circle", "r", LinkKind.Writes);
        AssertLink(g, "area", "r", LinkKind.Reads);
    }

    // ---------------------------------------------------------------- Kotlin

    private const string KotlinSource = """
        package shop

        const val MAX_ITEMS = 10

        data class Item(val name: String, val price: Double)

        class Basket(private val owner: String) {
            private val items = mutableListOf<Item>()
            var discount = 0.0

            fun add(item: Item) {
                if (items.size >= MAX_ITEMS) return
                items.add(item)
                notifyChanged()
            }

            fun total(): Double = items.sumOf { it.price } * (1 - discount)

            private fun notifyChanged() {
                println("changed")
            }

            private fun unused() {}

            override fun toString() = "Basket"

            companion object {
                fun empty(): Basket = Basket("nobody")
            }
        }

        fun main() {
            val basket = Basket("me")
            basket.add(Item("apple", 1.0))
            println(basket.total())
        }
        """;

    [Fact]
    public void Kotlin_classes_funs_properties_and_companions_are_linked()
    {
        using var t = new TempFolder();
        t.Text("Basket.kt", KotlinSource);

        var g = Analyze(t);

        AssertLink(g, "add", "items", LinkKind.Reads);
        AssertLink(g, "add", "MAX_ITEMS", LinkKind.Reads);
        AssertLink(g, "add", "notifyChanged", LinkKind.Calls);
        AssertLink(g, "total", "discount", LinkKind.Reads);  // expression-bodied fun
        AssertLink(g, "main", "Basket", LinkKind.Creates);
        AssertLink(g, "main", "Item", LinkKind.Creates);
        AssertLink(g, "main", "add", LinkKind.Calls);
        AssertLink(g, "empty", "Basket", LinkKind.Creates);  // inside companion object
        Assert.Equal("Basket", g.Symbols.Single(s => s.Name == "empty").Container);
        Assert.Equal("Item", g.Symbols.Single(s => s.Name == "price").Container); // data class property
    }

    [Fact]
    public void Kotlin_unused_code_and_end_lines()
    {
        using var t = new TempFolder();
        t.Text("Basket.kt", KotlinSource);

        var g = Analyze(t);
        string? Why(string name) => g.Symbols.Single(s => s.Name == name).UnusedReason;

        Assert.Equal("nothing calls it", Why("unused"));
        Assert.Null(Why("toString")); // override
        Assert.Null(Why("main"));
        Assert.Null(Why("notifyChanged"));
        var add = g.Symbols.Single(s => s.Name == "add");
        Assert.Equal(add.Line + 4, add.EndLine);
        var total = g.Symbols.Single(s => s.Name == "total");
        Assert.Equal(total.Line, total.EndLine);
    }

    [Fact]
    public void Maven_and_Gradle_output_folders_are_skipped()
    {
        using var t = new TempFolder();
        t.Text("src/main/java/App.java", "class App {}");
        t.Text("target/generated/Gen.java", "class Gen {}");
        t.Text("build/tmp/Gen.kt", "class Gen");
        t.Text(".gradle/cache/X.kt", "class X");

        var root = Scanner.Scan(t.Path, null, CancellationToken.None);

        Assert.Equal(new[] { "App.java" }, CodeAnalyzer.CodeFilesIn(root).Select(f => f.Name));
    }

    // ---------------------------------------------------------------- Boxes view (one box per file)

    [Fact]
    public async Task Boxes_view_has_one_box_per_file_and_arrows_between_them()
    {
        using var t = new TempFolder();
        var app = t.Text("app.py", "from helpers import greet\n\ndef main():\n    greet()\n    greet()\n\nmain()\n");
        var helpers = t.Text("lib/helpers.py", "def greet():\n    print('hi')\n\ndef unused():\n    pass\n");
        var vm = new CodeMapViewModel(t.Path, new[] { app, helpers }, null);
        await vm.LoadAsync();

        vm.ViewKind = 1;

        var map = vm.FileMap!;
        Assert.Equal(2, map.Boxes.Count);
        var edge = Assert.Single(map.Edges);
        Assert.Equal("app.py", edge.From.Name);
        Assert.Equal("helpers.py", edge.To.Name);
        Assert.Equal(2, edge.Count); // greet() is called twice
        var helpersBox = map.Boxes.Single(b => b.Name == "helpers.py");
        Assert.Equal("lib", helpersBox.Folder);
        Assert.Equal(new[] { "greet", "unused" }, helpersBox.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Box_rows_list_classes_with_their_members_and_cap_long_files()
    {
        using var t = new TempFolder();
        var shop = t.Text("shop.py", "def helper():\n    pass\n\nclass Shop:\n    def buy(self):\n        pass\n");
        var many = t.Text("many.py", string.Concat(Enumerable.Range(1, 20).Select(i => $"def f{i}():\n    pass\n\n")));
        var vm = new CodeMapViewModel(t.Path, new[] { shop, many }, null);
        await vm.LoadAsync();

        vm.ViewKind = 1;

        var shopBox = vm.FileMap!.Boxes.Single(b => b.Name == "shop.py");
        Assert.Equal(new[] { "Shop", "buy", "helper" }, shopBox.Rows.Select(r => r.Name));
        var manyBox = vm.FileMap.Boxes.Single(b => b.Name == "many.py");
        Assert.Equal(CodeMapViewModel.MaxRowsPerBox, manyBox.Rows.Count);
        Assert.Equal(20 - CodeMapViewModel.MaxRowsPerBox, manyBox.HiddenRows);
    }

    [Fact]
    public async Task Boxes_view_focus_mode_shows_only_nearby_files()
    {
        using var t = new TempFolder();
        var files = new[]
        {
            t.Text("a.py", "def fa():\n    fb()\n"),
            t.Text("b.py", "def fb():\n    fc()\n"),
            t.Text("c.py", "def fc():\n    pass\n"),
            t.Text("d.py", "def fd():\n    pass\n"),
        };
        var vm = new CodeMapViewModel(t.Path, files, null);
        await vm.LoadAsync();
        vm.ViewKind = 1;
        Assert.Equal(4, vm.FileMap!.Boxes.Count);

        vm.Selected = vm.ListItems.Single(s => s.Name == "fa");
        vm.FocusMode = 1;
        Assert.Equal(new[] { "a.py", "b.py" }, vm.FileMap!.Boxes.Select(b => b.Name).OrderBy(n => n));

        vm.FocusMode = 2;
        Assert.Equal(new[] { "a.py", "b.py", "c.py" }, vm.FileMap!.Boxes.Select(b => b.Name).OrderBy(n => n));
    }
}
