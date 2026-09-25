using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FolderMap.Models;
using FolderMap.Services;
using FolderMap.ViewModels;

namespace FolderMap.Views;

public partial class CodeMapWindow : Window
{
    // Below these widths the side panels shrink or hide so the graph keeps enough room.
    private const double HideListWidth = 860;
    private const double NarrowDetailsWidth = 1100;

    private bool? _listHidden;

    public CodeMapWindow()
    {
        InitializeComponent();

        Graph.SymbolDoubleClicked += (_, symbol) => OpenInEditor(symbol);
        Boxes.OpenRequested += (_, target) => OpenInEditor(target.File, target.Line);
        Opened += async (_, _) =>
        {
            ApplyLayout(ClientSize);
            if (DataContext is CodeMapViewModel vm) await vm.LoadAsync();
        };
        Closed += (_, _) => (DataContext as CodeMapViewModel)?.Cancel();
    }

    private CodeMapViewModel Vm => (CodeMapViewModel)DataContext!;

    // ---------- adapting to the window size ----------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) ApplyLayout(ClientSize);
    }

    private void ApplyLayout(Size size)
    {
        if (size.Width <= 0 || MainGrid is null) return;

        MainGrid.ColumnDefinitions[4].Width = new GridLength(size.Width < NarrowDetailsWidth ? 250 : 320);

        bool hideList = size.Width < HideListWidth;
        if (hideList == _listHidden) return;
        _listHidden = hideList;
        ListPanel.IsVisible = !hideList;
        LeftSplitter.IsVisible = !hideList;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(hideList ? 0 : 260);
        MainGrid.ColumnDefinitions[1].Width = new GridLength(hideList ? 0 : 5);
    }

    // ---------- selection ----------

    private void OnSymbolListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SymbolList.SelectedItem is CodeSymbol symbol) Vm.Selected = symbol;
    }

    private void OnLinkSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The lists are rebuilt when the selection changes, so there's no need to clear them.
        // Posted so the list isn't replaced while it's still handling its own selection event.
        if (sender is ListBox { SelectedItem: LinkItem item })
            Dispatcher.UIThread.Post(() => Vm.Selected = item.Other);
    }

    private void OnPathStepSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: PathStep step })
            Dispatcher.UIThread.Post(() => Vm.Selected = step.Target);
    }

    // ---------- buttons ----------

    private void OnStartPathClick(object? sender, RoutedEventArgs e) => Vm.StartPath();

    private void OnFindPathClick(object? sender, RoutedEventArgs e) => Vm.FindPathToSelected();

    private void OnClearPathClick(object? sender, RoutedEventArgs e) => Vm.ClearPath();

    private void OnFitClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsBoxesView) Boxes.FitToView();
        else Graph.FitToView();
    }

    private void OnFocusClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.FocusMode == 0) Vm.FocusMode = 1;
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } symbol) OpenInEditor(symbol);
    }

    private void OpenInEditor(CodeSymbol symbol) => OpenInEditor(symbol.File, symbol.Line);

    private async void OpenInEditor(string file, int line)
    {
        try
        {
            FileOps.OpenInEditor(file, line);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Couldn't open the file", ex.Message);
        }
    }
}
