using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using livepaper.ViewModels;

namespace livepaper.Views;

public partial class MainWindow : Window
{
    // Layout constants
    private const double MinCardWidthLandscape = 250;
    private const double MinCardWidthPortrait = 160;
    private const double CardHorizontalMargin = 8;
    private const double PlaylistItemWidth = 100;
    private const double PlaylistItemSpacing = 6;
    private const double PlaylistItemStride = PlaylistItemWidth + PlaylistItemSpacing;

    private double _lastRepeaterWidth;

    // Playlist drag state
    private WallpaperCardViewModel? _dragCard;
    private bool _isDragging;
    private Point _dragStartPos;

    public MainWindow()
    {
        InitializeComponent();
        BrowseScrollViewer.ScrollChanged += OnBrowseScrollChanged;
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnKeyDown;
        new SmoothScroller(BrowseScrollViewer);
        new SmoothScroller(LibraryScrollViewer);
        new SmoothScroller(SettingsScrollViewer);
        new SmoothScroller(PlaylistScrollViewer);
        Loaded += (_, _) =>
        {
            BrowseItemsRepeater.SizeChanged += (_, _) => UpdateCardThumbnailHeight();
            LibraryItemsRepeater.SizeChanged += (_, _) => UpdateCardThumbnailHeight();
            if (Vm != null) Vm.CardLayoutChanged = UpdateCardThumbnailHeight;
            UpdateCardThumbnailHeight();
        };

        MainTabControl.SelectionChanged += (_, _) =>
            PlaylistPanel.IsVisible = MainTabControl.SelectedIndex == 1;

        this.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;
    private MainWindowViewModel? _boundVm;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm.PickFolderDialog = null;
            _boundVm.PickVideoDialog = null;
            _boundVm.CopyToClipboard = null;
            _boundVm = null;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.PickFolderDialog = PickFolderDialogAsync;
            vm.PickVideoDialog = PickVideoDialogAsync;
            vm.CopyToClipboard = async text =>
            {
                // Try wl-copy first — it forks a daemon that keeps holding
                // the clipboard selection after livepaper exits. Avalonia's
                // own clipboard releases ownership on app close, so without
                // wl-copy (or a separate clipboard manager) the snippet is
                // lost as soon as the user closes the window.
                if (await TryWlCopyAsync(text)) return;
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(text);
            };
            _boundVm = vm;
        }
    }

    private static async Task<bool> TryWlCopyAsync(string text)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("wl-copy")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            await proc.StandardInput.WriteAsync(text);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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

    private static readonly FilePickerFileType WallpaperFileType = new("Wallpaper files")
    {
        Patterns = ["*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi", "*.gif", "*.png", "*.jpg", "*.jpeg", "*.webp"]
    };

    private async Task<string?> PickVideoDialogAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Wallpaper",
            FileTypeFilter = [WallpaperFileType],
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
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
            if (source is ScrollBar || (source as Visual)?.FindAncestorOfType<ScrollBar>() != null) return;
            var card = FindAncestorDataContext<WallpaperCardViewModel>(source, LibraryScrollViewer);
            if (card == null) { Vm?.DeselectAllLibrary(); return; }
            Vm?.SelectCard(card, shift, ctrl);
        }
        else if (IsWithin(source, BrowseScrollViewer))
        {
            if (IsWithinButton(source, BrowseScrollViewer)) return;
            if (source is ScrollBar || (source as Visual)?.FindAncestorOfType<ScrollBar>() != null) return;
            var card = FindAncestorDataContext<WallpaperCardViewModel>(source, BrowseScrollViewer);
            if (card == null) { Vm?.DeselectAllBrowse(); return; }
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

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        DragPreviewCanvas.IsVisible = false;
        PlaylistDropIndicator.IsVisible = false;
        if (_dragCard != null) _dragCard.IsGifActive = false;
        _dragCard = null;
        _isDragging = false;
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
        else if (Vm != null)
        {
            Vm.PlayFromCardCommand.Execute(_dragCard);
        }

        DragPreviewCanvas.IsVisible = false;
        PlaylistDropIndicator.IsVisible = false;
        if (_dragCard != null) _dragCard.IsGifActive = false;
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

    private void UpdateCardThumbnailHeight()
    {
        if (Vm == null) return;
        var width = BrowseItemsRepeater.Bounds.Width > 0
            ? BrowseItemsRepeater.Bounds.Width
            : LibraryItemsRepeater.Bounds.Width;
        if (width > 0) _lastRepeaterWidth = width;
        else width = _lastRepeaterWidth;
        if (width <= 0) return;
        (double minCardWidth, double ratio) = Vm.ThumbnailAspect switch
        {
            "1:1"  => (MinCardWidthPortrait,  1.0),
            "16:9" => (MinCardWidthLandscape, 9.0 / 16.0),
            _      => (210.0,                 150.0 / 210.0),
        };
        double sizeMultiplier = Vm.CardSize switch
        {
            "Small" => 0.65,
            "Large" => 1.5,
            _       => 1.0,
        };
        minCardWidth *= sizeMultiplier;
        Vm.CardMinWidth = minCardWidth;
        Vm.CardButtonFontSize = Math.Clamp(Math.Round(13.0 * minCardWidth / 210.0), 9, 13);
        int cols = Math.Max(1, (int)Math.Floor(width / minCardWidth));
        double cardWidth = width / cols - CardHorizontalMargin;
        Vm.CardThumbnailHeight = Math.Round(cardWidth * ratio);
    }

    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is StyledElement se && se.DataContext is WallpaperCardViewModel card)
            card.IsGifActive = true;
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is StyledElement se && se.DataContext is WallpaperCardViewModel card && card != _dragCard)
            card.IsGifActive = false;
    }

    private void OnBrowseScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.IsLoading || vm.NoMorePages || !vm.SelectedSource.SupportsPagination) return;

        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            vm.LoadMoreCommand.Execute(null);
    }

    private sealed class SmoothScroller
    {
        private readonly ScrollViewer _sv;
        private double _velocity;
        private bool _animating;
        private TimeSpan? _lastTime;
        private const double Impulse = 80.0;
        private const double Friction = 0.85;
        private const double StopThreshold = 0.1;
        private const double MaxVelocity = 2500.0;

        public SmoothScroller(ScrollViewer sv)
        {
            _sv = sv;
            sv.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            // Let sliders and spinners handle their own wheel events
            if (e.Source is Slider or NumericUpDown) return;
            if ((e.Source as Visual)?.FindAncestorOfType<Slider>() != null) return;
            if ((e.Source as Visual)?.FindAncestorOfType<NumericUpDown>() != null) return;

            double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            _velocity = Math.Clamp(_velocity - delta * Impulse, -MaxVelocity, MaxVelocity);
            e.Handled = true;

            if (!_animating)
            {
                _animating = true;
                _lastTime = null;
                TopLevel.GetTopLevel(_sv)?.RequestAnimationFrame(OnFrame);
            }
        }

        private void OnFrame(TimeSpan time)
        {
            if (!_animating) return;

            double dt = _lastTime.HasValue
                ? Math.Min((time - _lastTime.Value).TotalMilliseconds, 64)
                : 16.0;
            _lastTime = time;

            _velocity *= Math.Pow(Friction, dt / 16.0);

            if (Math.Abs(_velocity) < StopThreshold)
            {
                _animating = false;
                _velocity = 0;
                return;
            }

            var currentOffset = _sv.Offset;
            bool isHorizontal = _sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                               _sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled;

            if (isHorizontal)
            {
                var maxX = Math.Max(0, _sv.Extent.Width - _sv.Viewport.Width);
                var newX = Math.Clamp(currentOffset.X + _velocity * (dt / 16.0), 0, maxX);
                if (newX <= 0 || newX >= maxX) _velocity = 0;
                _sv.Offset = new Vector(newX, currentOffset.Y);
            }
            else
            {
                var maxY = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
                var newY = Math.Clamp(currentOffset.Y + _velocity * (dt / 16.0), 0, maxY);
                if (newY <= 0 || newY >= maxY) _velocity = 0;
                _sv.Offset = new Vector(currentOffset.X, newY);
            }

            TopLevel.GetTopLevel(_sv)?.RequestAnimationFrame(OnFrame);
        }
    }
}
