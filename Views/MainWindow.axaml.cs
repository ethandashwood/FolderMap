using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using FolderMap.Models;
using FolderMap.Services;
using FolderMap.ViewModels;

namespace FolderMap.Views;

/// <summary>
/// Code-behind handles things that need a window: folder pickers, dialogs and
/// keeping the tree's highlighted item in step with the charts.
/// </summary>
public partial class MainWindow : Window
{
    // Below these widths the layout switches to its narrower forms.
    private const double NarrowWidth = 820;   // folder tree hides behind the "Folders" button
    private const double CompactWidth = 1100; // hint text hides, action buttons move to their own row

    private MainViewModel? _subscribedVm;
    private bool _syncingTree;
    private bool? _isNarrow;
    private bool? _isCompact;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            FitToScreen();
            ApplyLayout(ClientSize);
        };
    }

    // =====================================================================
    // Adapting to the window / screen size
    // =====================================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) ApplyLayout(ClientSize);
    }

    /// <summary>On small or high-scaling screens, shrink the window so it all fits on screen.</summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        double scale = screen.Scaling;
        var area = screen.WorkingArea; // physical pixels, excludes the taskbar
        double maxWidth = area.Width / scale * 0.95;
        double maxHeight = area.Height / scale * 0.92;
        if (Width <= maxWidth && Height <= maxHeight) return;

        Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
        Position = new PixelPoint(
            area.X + (int)((area.Width - Width * scale) / 2),
            area.Y + (int)((area.Height - Height * scale) / 2));
    }

    private void ApplyLayout(Size size)
    {
        // ClientSize can change while the window is still being constructed.
        if (size.Width <= 0 || ActionButtons is null || ExplorerGrid is null) return;

        bool compact = size.Width < CompactWidth;
        if (compact != _isCompact)
        {
            _isCompact = compact;
            Classes.Set("compact", compact); // hides the long hint text (see Window.Styles)

            // Wide: buttons sit to the right of the selected path.
            // Compact: buttons drop to a second row, where the WrapPanel can wrap them.
            Grid.SetRow(ActionButtons, compact ? 1 : 0);
            Grid.SetColumn(ActionButtons, compact ? 0 : 2);
            Grid.SetColumnSpan(ActionButtons, compact ? 3 : 1);
            ActionButtons.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(10, 0, 0, 0);
        }

        bool narrow = size.Width < NarrowWidth;
        if (narrow != _isNarrow)
        {
            _isNarrow = narrow;
            TreeToggle.IsVisible = narrow;
            TreeToggle.IsChecked = false;
            UpdateTreeColumn();
        }
    }

    private void OnTreeToggleChanged(object? sender, RoutedEventArgs e) => UpdateTreeColumn();

    /// <summary>Wide: tree always shown. Narrow: tree only while the "Folders" button is pressed.</summary>
    private void UpdateTreeColumn()
    {
        bool showTree = _isNarrow != true || TreeToggle.IsChecked == true;
        double treeWidth = _isNarrow == true ? Math.Min(260, ClientSize.Width * 0.45) : 320;

        FolderTree.IsVisible = showTree;
        TreeSplitter.IsVisible = showTree;
        ExplorerGrid.ColumnDefinitions[0].Width = showTree ? new GridLength(treeWidth) : new GridLength(0);
        ExplorerGrid.ColumnDefinitions[1].Width = showTree ? new GridLength(5) : new GridLength(0);
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribedVm != null) _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = DataContext as MainViewModel;
        if (_subscribedVm != null) _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    // When the charts drill into a folder, highlight the same folder in the tree.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentFolder)) return;
        var folder = Vm.CurrentFolder;
        if (folder is null || ReferenceEquals(FolderTree.SelectedItem, folder)) return;

        // Post so the tree has expanded the parents before we select.
        Dispatcher.UIThread.Post(() =>
        {
            _syncingTree = true;
            try { FolderTree.SelectedItem = folder; }
            catch { /* not realised yet - harmless */ }
            finally { _syncingTree = false; }
        }, DispatcherPriority.Background);
    }

    // ---------- theme ----------

    /// <summary>
    /// Switches the whole app between following Windows, light and dark. Setting it on the
    /// Application (not this window) means open Code map windows switch too.
    /// </summary>
    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Use the sender: this also fires while the window is loading, before ThemePicker is set.
        if (Application.Current is not { } app || sender is not ComboBox picker) return;
        app.RequestedThemeVariant = picker.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default, // follow the Windows setting
        };
    }

    // ---------- scanning ----------

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder or drive to scan",
            AllowMultiple = false,
        });
        var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (path is null) return;

        Vm.ScanPath = path;
        await Vm.ScanCommand.ExecuteAsync(null);
    }

    // ---------- selection ----------

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree) return;
        if (FolderTree.SelectedItem is FolderNode folder)
        {
            Vm.CurrentFolder = folder;
            Vm.SelectedEntry = folder;
        }
        else if (FolderTree.SelectedItem is MoreFoldersItem more)
        {
            Vm.CurrentFolder = more.Parent; // the list on the right shows every subfolder
        }
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        FsNode? node = grid.SelectedItem switch
        {
            FsNode n => n,
            DuplicateRow row => row.File,
            _ => null,
        };
        if (node != null) Vm.SelectedEntry = node;
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = Vm.SelectedEntry;
        if (node is null) return;

        if (MainTabs.SelectedIndex == 0)
        {
            // Explorer tab: open folders in place, open files with their default app.
            if (node is FolderNode folder) Vm.CurrentFolder = folder;
            else OpenSafely(node);
        }
        else
        {
            GoToInExplorer(node);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void GoToInExplorer(FsNode node)
    {
        var folder = node as FolderNode ?? node.Parent;
        if (folder is null) return;
        Vm.CurrentFolder = folder;
        Vm.SelectedEntry = node;
        MainTabs.SelectedIndex = 0;
        if (_isNarrow == true) TreeToggle.IsChecked = true; // "Show in tree" should actually show it
    }

    // ---------- actions on the selected item ----------

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { } node) OpenSafely(node);
    }

    /// <summary>Opens a file, but asks first if "opening" it would actually run a program or script.</summary>
    private async void OpenSafely(FsNode node)
    {
        if (!node.IsFolder && FileOps.IsRunnable(node.Name) &&
            !await Dialogs.ConfirmAsync(this, "Run this?",
                $"\"{node.Name}\" is a program or script. Opening it will RUN it, not just show it.\n\n" +
                "Only do this if you trust it. To look inside instead, use Show in Explorer and open it with an editor.",
                "Run it"))
            return;

        Run(() => FileOps.Open(node.FullPath));
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { } node) Run(() => FileOps.ShowInExplorer(node.FullPath));
    }

    private void OnGoToClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { } node) GoToInExplorer(node);
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedEntry;
        if (node is null || !await EnsureNotRoot(node)) return;

        var newName = await Dialogs.PromptAsync(this, "Rename", $"New name for \"{node.Name}\":", node.Name);
        newName = newName?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == node.Name) return;

        try
        {
            FileOps.Rename(node, newName);
            Vm.OnRenamed(node, newName);
            Vm.Status = $"Renamed to {newName}";
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't rename", ex.Message);
        }
    }

    private async void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedEntry;
        if (node is null || !await EnsureNotRoot(node)) return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Move \"{node.Name}\" to…",
            AllowMultiple = false,
        });
        var destination = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (destination is null) return;

        if (node is FolderNode folder &&
            (Path.GetFullPath(destination) + Path.DirectorySeparatorChar).StartsWith(
                folder.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.InfoAsync(this, "Can't move there", "A folder can't be moved inside itself.");
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, "Move", $"Move \"{node.Name}\" ({node.SizeText}) to\n{destination}?", "Move"))
            return;

        try
        {
            FileOps.Move(node, destination);
            Vm.OnMoved(node, destination);
            Vm.Status = $"Moved {node.Name} to {destination}";
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't move", ex.Message);
        }
    }

    private async void OnRecycleClick(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedEntry;
        if (node is null || !await EnsureNotRoot(node)) return;

        var what = node is FolderNode f ? $"the folder \"{node.Name}\" and its {f.FileCount:N0} files" : $"\"{node.Name}\"";
        var message = $"Send {what} ({node.SizeText}) to the Recycle Bin?\n\nYou can restore it from the Recycle Bin later.";
        if (FileOps.MayNotHaveRecycleBin(node.FullPath))
        {
            message = $"Send {what} ({node.SizeText}) to the Recycle Bin?\n\n" +
                      "⚠ This looks like a USB, network or removable drive, which usually has NO Recycle Bin. " +
                      "If Windows can't recycle it, it will ask you before deleting it permanently.";
        }
        if (!await Dialogs.ConfirmAsync(this, "Send to Recycle Bin", message, "Recycle"))
            return;

        try
        {
            var name = node.Name;
            FileOps.Recycle(node);
            Vm.OnRemoved(node);
            Vm.Status = $"Sent {name} to the Recycle Bin";
        }
        catch (OperationCanceledException)
        {
            Vm.Status = "Nothing was deleted."; // the user said no to Windows' "delete permanently?" prompt
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't recycle", ex.Message);
        }
    }

    private async void OnAnalyseCodeClick(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedEntry;
        // A single file is analysed together with the rest of its folder, so its links to
        // other files show up; the window then starts focused on that file.
        var folder = node as FolderNode ?? node?.Parent;
        if (folder is null) return;

        var files = CodeAnalyzer.CodeFilesIn(folder).Select(f => f.FullPath).ToList();
        if (files.Count == 0)
        {
            await Dialogs.InfoAsync(this, "No code found",
                "There are no C#, Java, Kotlin, Python, JavaScript or TypeScript files here (dependency and build folders are skipped).");
            return;
        }

        if (files.Count > CodeAnalyzer.MaxFiles &&
            !await Dialogs.ConfirmAsync(this, "Lots of code",
                $"This folder has {files.Count:N0} code files. Only the first {CodeAnalyzer.MaxFiles:N0} will be analysed, " +
                "and it may take a minute. Pick a smaller folder for a clearer map.\n\nContinue anyway?", "Analyse"))
            return;

        var focusFile = node is FileNode file ? file.FullPath : null;
        // XAML / HTML files are read too, so handlers and bindings they use don't look "unused".
        var markupFiles = CodeAnalyzer.MarkupFilesIn(folder).Select(f => f.FullPath).ToList();
        var window = new CodeMapWindow
        {
            DataContext = new CodeMapViewModel(folder.FullPath, files, focusFile, markupFiles),
        };
        window.Show(this);
    }

    private async Task<bool> EnsureNotRoot(FsNode node)
    {
        if (node.Parent != null) return true;
        await Dialogs.InfoAsync(this, "Not allowed", "That's the folder you scanned. Pick something inside it instead.");
        return false;
    }

    private async void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { await Dialogs.InfoAsync(this, "Something went wrong", ex.Message); }
    }
}
