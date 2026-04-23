using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using livepaper.ViewModels;

namespace livepaper.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType JsonFileType =
        new("JSON Playlist") { Patterns = ["*.json"] };

    // Playlist drag state
    private WallpaperCardViewModel? _dragCard;
    private bool _isDragging;
    private Point _dragStartPos;

    private const double PlaylistItemWidth = 100;
    private const double PlaylistItemSpacing = 6;
    private const double PlaylistItemStride = PlaylistItemWidth + PlaylistItemSpacing;

    public MainWindow()
    {
        InitializeComponent();
        BrowseScrollViewer.ScrollChanged += OnBrowseScrollChanged;
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnKeyDown;

        this.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;
    private MainWindowViewModel? _boundVm;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm.OpenSaveDialog = null;
            _boundVm.OpenLoadDialog = null;
            _boundVm.PickFolderDialog = null;
            _boundVm.CopyToClipboard = null;
            _boundVm = null;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.OpenSaveDialog = OpenSaveDialogAsync;
            vm.OpenLoadDialog = OpenLoadDialogAsync;
            vm.PickFolderDialog = PickFolderDialogAsync;
            vm.CopyToClipboard = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetValueAsync(DataFormat.Text, text);
            };
            _boundVm = vm;
        }
    }

    private async Task<string?> OpenSaveDialogAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Playlist",
            SuggestedFileName = "playlist.json",
            FileTypeChoices = [JsonFileType]
        });
        return file?.Path.LocalPath;
    }

    private async Task<string?> PickFolderDialogAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Wallpaper Engine Workshop Folder",
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task<string?> OpenLoadDialogAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Playlist",
            FileTypeFilter = [JsonFileType],
            AllowMultiple = false
        });
        return files.FirstOrDefault()?.Path.LocalPath;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm?.PreviewCard != null)
        {
            Vm.ClosePreviewCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            if (MainTabControl.SelectedIndex == 0)
                Vm?.SelectAllBrowseCommand.Execute(null);
            else if (MainTabControl.SelectedIndex == 1)
                Vm?.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (IsWithin(source, PlaylistScrollViewer))
        {
            if (IsWithinButton(source, PlaylistScrollViewer)) return;
            var card = FindAncestorDataContext<WallpaperCardViewModel>(source, PlaylistScrollViewer);
            if (card == null) return;
            _dragCard = card;
            _isDragging = false;
            _dragStartPos = e.GetPosition(this);
        }
        else if (IsWithin(source, LibraryScrollViewer))
        {
            if (IsWithinButton(source, LibraryScrollViewer)) return;
            var card = FindAncestorDataContext<WallpaperCardViewModel>(source, LibraryScrollViewer);
            if (card == null) return;
            Vm?.SelectCard(card, shift, ctrl);
        }
        else if (IsWithin(source, BrowseScrollViewer))
        {
            if (IsWithinButton(source, BrowseScrollViewer)) return;
            var card = FindAncestorDataContext<WallpaperCardViewModel>(source, BrowseScrollViewer);
            if (card == null) return;
            Vm?.SelectBrowseCard(card, shift, ctrl);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard == null) return;

        var windowPos = e.GetPosition(this);

        if (!_isDragging)
        {
            var dx = windowPos.X - _dragStartPos.X;
            var dy = windowPos.Y - _dragStartPos.Y;
            if (dx * dx + dy * dy < 36) return; // 6px threshold

            _isDragging = true;
            DragPreviewImage.Source = _dragCard.ThumbnailSource;
            DragPreviewCanvas.IsVisible = true;
        }

        Canvas.SetLeft(DragPreviewBorder, windowPos.X - 25);
        Canvas.SetTop(DragPreviewBorder, windowPos.Y - 22);

        var svPos = e.GetPosition(PlaylistScrollViewer);
        if (svPos.X >= 0 && svPos.X <= PlaylistScrollViewer.Bounds.Width
            && svPos.Y >= 0 && svPos.Y <= PlaylistScrollViewer.Bounds.Height)
        {
            int idx = GetPlaylistInsertIndex(svPos.X + PlaylistScrollViewer.Offset.X);
            UpdateDropIndicator(idx);
        }
        else
        {
            PlaylistDropIndicator.IsVisible = false;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard == null) return;

        if (_isDragging && Vm != null)
        {
            var svPos = e.GetPosition(PlaylistScrollViewer);
            bool inStrip = svPos.X >= 0 && svPos.X <= PlaylistScrollViewer.Bounds.Width
                        && svPos.Y >= 0 && svPos.Y <= PlaylistScrollViewer.Bounds.Height;
            if (inStrip)
            {
                int insertIdx = GetPlaylistInsertIndex(svPos.X + PlaylistScrollViewer.Offset.X);
                int fromIdx = Vm.PlaylistItems.IndexOf(_dragCard);
                if (fromIdx >= 0)
                    Vm.MovePlaylistItem(fromIdx, insertIdx);
            }
        }

        DragPreviewCanvas.IsVisible = false;
        PlaylistDropIndicator.IsVisible = false;
        _dragCard = null;
        _isDragging = false;
    }

    private int GetPlaylistInsertIndex(double x)
    {
        int count = Vm?.PlaylistItems.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (x < i * PlaylistItemStride + PlaylistItemWidth / 2.0)
                return i;
        }
        return count;
    }

    private void UpdateDropIndicator(int insertIndex)
    {
        double x = insertIndex * PlaylistItemStride - PlaylistScrollViewer.Offset.X - 1;
        if (x < -2 || x > PlaylistScrollViewer.Bounds.Width + 2)
        {
            PlaylistDropIndicator.IsVisible = false;
            return;
        }
        PlaylistDropIndicator.IsVisible = true;
        PlaylistDropIndicator.Margin = new Thickness(x, 0, 0, 0);
    }

    private static bool IsWithin(Visual? v, Visual ancestor)
    {
        while (v != null)
        {
            if (v == ancestor) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static bool IsWithinButton(Visual? v, Visual stopAt)
    {
        while (v != null && v != stopAt)
        {
            if (v is Button) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static T? FindAncestorDataContext<T>(Visual? v, Visual? stopAt = null) where T : class
    {
        while (v != null && v != stopAt)
        {
            if (v is StyledElement se && se.DataContext is T ctx) return ctx;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoading)
            && sender is MainWindowViewModel vm
            && !vm.IsLoading)
        {
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
