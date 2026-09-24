using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    private MainViewModel? _subscribedVm;
    private bool _syncingTree;

    public MainWindow()
    {
        InitializeComponent();
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
            else Run(() => FileOps.Open(node.FullPath));
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
    }

    // ---------- actions on the selected item ----------

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { } node) Run(() => FileOps.Open(node.FullPath));
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
        if (!await Dialogs.ConfirmAsync(this, "Send to Recycle Bin",
                $"Send {what} ({node.SizeText}) to the Recycle Bin?\n\nYou can restore it from the Recycle Bin later.",
                "Recycle"))
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
            // User cancelled the Windows prompt.
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't recycle", ex.Message);
        }
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
