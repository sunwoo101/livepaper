using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using livepaper.ViewModels;

namespace livepaper.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BrowseScrollViewer.ScrollChanged += OnBrowseScrollChanged;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.PreviewCard != null)
        {
            vm.ClosePreviewCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoading)
            && sender is MainWindowViewModel vm
            && !vm.IsLoading)
        {
            // After each load, check if content fills the viewport.
            // Post at Background priority so layout has time to update first.
            Dispatcher.UIThread.Post(CheckFillViewport, DispatcherPriority.Background);
        }
    }

    private void CheckFillViewport()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.IsLoading || vm.NoMorePages || !vm.SelectedSource.SupportsPagination) return;

        if (BrowseScrollViewer.Extent.Height <= BrowseScrollViewer.Viewport.Height)
            vm.LoadMoreCommand.Execute(null);
    }

    private void OnBrowseScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.IsLoading || vm.NoMorePages || !vm.SelectedSource.SupportsPagination) return;

        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            vm.LoadMoreCommand.Execute(null);
    }
}
