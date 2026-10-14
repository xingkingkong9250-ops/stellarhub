using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W1 main window. Code-behind stays limited to view concerns:
/// selection mirroring, focus requests, zoom debounce and mouse gestures.
/// </summary>
public partial class ShellWindow : Window
{
    private const int ZOOM_DEBOUNCE_MS = 150;
    private const int DRAG_THRESHOLD_SQR = 25; // 5px × 5px

    private readonly ShellViewModel _viewModel;
    private readonly DispatcherTimer _gridZoomTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(ZOOM_DEBOUNCE_MS),
    };
    private readonly DispatcherTimer _listZoomTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(ZOOM_DEBOUNCE_MS),
    };
    private readonly DispatcherTimer _browseZoomTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(ZOOM_DEBOUNCE_MS),
    };
    /// <summary>Load the next page this many pixels before the bottom (M4-D).</summary>
    private const double LOAD_MORE_THRESHOLD_PX = 500;
    private ScrollViewer? _galleryScroll;
    private bool _syncingSelection;

    private Point _dragStart;
    private bool _dragPending;

    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += (_, args) =>
        {
            App.WriteLog($"[ShellWindow] Closing event. Cancel={args.Cancel}");
        };

        _gridZoomTimer.Tick += (_, _) =>
        {
            _gridZoomTimer.Stop();
            _viewModel.Gallery.GridZoom = (int)GridZoomSlider.Value;
        };
        _listZoomTimer.Tick += (_, _) =>
        {
            _listZoomTimer.Stop();
            _viewModel.Gallery.ListZoom = (int)ListZoomSlider.Value;
        };
        _browseZoomTimer.Tick += (_, _) =>
        {
            _browseZoomTimer.Stop();
            _viewModel.Gallery.BrowseZoom = (int)BrowseZoomSlider.Value;
        };
        GridZoomSlider.Value = _viewModel.Gallery.GridZoom;
        ListZoomSlider.Value = _viewModel.Gallery.ListZoom;
        BrowseZoomSlider.Value = _viewModel.Gallery.BrowseZoom;
        GridZoomSlider.ValueChanged += (_, _) => Restart(_gridZoomTimer);
        ListZoomSlider.ValueChanged += (_, _) => Restart(_listZoomTimer);
        BrowseZoomSlider.ValueChanged += (_, _) => Restart(_browseZoomTimer);

        _viewModel.TopBar.FocusSearchRequested += FocusSearch;

        GalleryList.PreviewMouseLeftButtonDown += GalleryList_PreviewMouseLeftButtonDown;
        GalleryList.MouseMove += GalleryList_MouseMove;
        GalleryList.Loaded += (_, _) => HookGalleryScroll();
    }

    // ─── v0.6.2: 自定义窗口 caption 按钮（替代 UseAeroCaptionButtons） ────
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        App.WriteLog("[ShellWindow] OnLoaded, calling InitializeAsync...");
        try
        {
            await _viewModel.InitializeAsync();
            App.WriteLog("[ShellWindow] InitializeAsync completed.");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[ShellWindow] InitializeAsync FAILED: {ex}");
            throw;
        }
    }

    // ─── Gallery endless scroll (M4-D server-side pagination) ─────────────

    private void HookGalleryScroll()
    {
        if (_galleryScroll is not null)
            _galleryScroll.ScrollChanged -= GalleryScroll_Changed;
        _galleryScroll = FindDescendant<ScrollViewer>(GalleryList);
        if (_galleryScroll is not null)
            _galleryScroll.ScrollChanged += GalleryScroll_Changed;
    }

    private void GalleryScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (_galleryScroll is null || _galleryScroll.ScrollableHeight <= 0)
            return;
        if (_galleryScroll.VerticalOffset < _galleryScroll.ScrollableHeight - LOAD_MORE_THRESHOLD_PX)
            return;
        _ = _viewModel.Gallery.LoadMoreAsync();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is T nested)
                return nested;
        }
        return null;
    }

    private void FocusSearch()
    {
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.SelectAll();
    }

    // v0.14.6: Enter 键立即提交搜索，不等待防抖定时器
    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.TopBar.CommitSearchNow();
            e.Handled = true;
        }
    }

    private void GalleryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            _viewModel.Gallery.SelectedItems.Clear();
            foreach (GalleryItemViewModel item in GalleryList.SelectedItems)
                _viewModel.Gallery.SelectedItems.Add(item);
            _viewModel.Gallery.NotifySelectionChanged();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>U6-A: mirrors the shared list selection into SharedGalleryViewModel.</summary>
    private void SharedGalleryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SharedGallery is not { } shared) return;
        shared.SelectedItems.Clear();
        foreach (GalleryItemViewModel item in SharedGalleryList.SelectedItems)
            shared.SelectedItems.Add(item);
        shared.NotifySelectionChanged();
    }

    private void GalleryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GalleryList.SelectedItem is null) return;
        if (_viewModel.OpenCommand.CanExecute(null))
            _viewModel.OpenCommand.Execute(null);
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Detail.OpenOriginalCommand.CanExecute(null))
            _viewModel.Detail.OpenOriginalCommand.Execute(null);
    }

    // ─── Gallery drag source ───────────────────────────────────────────────

    private void GalleryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only seed drag when the press is on a gallery item; the ListBox
        // still gets the click for selection purposes.
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is null) return;
        _dragStart = e.GetPosition(null);
        _dragPending = true;
    }

    private void GalleryList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragPending = false;
            return;
        }

        Point pos = e.GetPosition(null);
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;
        if (dx * dx + dy * dy < DRAG_THRESHOLD_SQR) return;

        _dragPending = false;
        if (_viewModel.Gallery.SelectedItems.Count == 0) return;

        var data = new DataObject(DataFormats.Serializable, _viewModel.Gallery.SelectedItems.ToList());
        DragDrop.DoDragDrop(GalleryList, data, DragDropEffects.Copy);
    }

    // ─── Collection drop target ─────────────────────────────────────────────

    private void OnCollectionDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CollectionNodeViewModel node) return;

        var items = ExtractGalleryItems(e.Data);
        if (items.Count == 0) return;

        if (_viewModel.AddSelectedToCollectionCommand.CanExecute(node))
        {
            _viewModel.AddSelectedToCollectionCommand.Execute(node);
            e.Handled = true;
        }
    }

    private static List<GalleryItemViewModel> ExtractGalleryItems(IDataObject data)
    {
        if (data.GetData(DataFormats.Serializable) is List<GalleryItemViewModel> list)
            return list;
        if (data.GetData(DataFormats.Serializable) is IEnumerable<GalleryItemViewModel> enumItems)
            return enumItems.ToList();
        if (data.GetData(typeof(GalleryItemViewModel)) is GalleryItemViewModel single)
            return [single];
        return [];
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        while (source is not null && source is not T)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        return source as T;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
