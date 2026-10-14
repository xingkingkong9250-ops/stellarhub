using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QL.World.ViewModels;

namespace QL.World.Views.Support;

/// <summary>
/// B2-3-BRW: pixel-scrolling virtualizing masonry (waterfall) panel.
/// Each item has uniform width (<see cref="ItemWidth"/>) but variable height
/// derived from the data item's <see cref="GalleryItemViewModel.AspectRatio"/>
/// (height/width). Items are placed into the column with the smallest running
/// height so the layout is balanced top-to-bottom (ArtStation-style feed).
///
/// Layout follows the same virtualization contract as
/// <see cref="VirtualizingTilePanel"/>: only containers intersecting the
/// vertical viewport are realized; the rest are recycled via the
/// ItemContainerGenerator. Container-lifecycle gaps closed there
/// (out-of-range detach, generator re-hook on async load, ItemsPanel-swap
/// foreign-container recovery) are mirrored here.
/// </summary>
public sealed class VirtualizingMasonryPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth), typeof(double), typeof(VirtualizingMasonryPanel),
            new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Horizontal gap between adjacent columns and vertical gap between stacked items.</summary>
    public const double Gap = 8.0;

    private Size _viewportSize;
    private Size _extentSize;
    private Point _scrollOffset;

    // Layout cache: one entry per item. Rebuilt when items change or column
    // count changes. MeasureOverride is O(N) arithmetic — fine for 1k+ items;
    // virtualization still skips container realization for off-screen items.
    private record struct ItemLayout(double Top, double Height, int Column);
    private List<ItemLayout>? _layout;
    private int _layoutColumnCount;
    private bool _genHooked;

    // v0.14.5: 自适应布局缓存（与 VirtualizingTilePanel 同源算法）
    private int _columns;
    private double _effectiveItemWidth;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    // ─── IScrollInfo plumbing ────────────────────────────────────────────────

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extentSize.Width;
    public double ExtentHeight => _extentSize.Height;
    public double ViewportWidth => _viewportSize.Width;
    public double ViewportHeight => _viewportSize.Height;
    public double HorizontalOffset => _scrollOffset.X;
    public double VerticalOffset => _scrollOffset.Y;

    public void SetHorizontalOffset(double offset) { /* masonry never scrolls horizontally */ }

    public void SetVerticalOffset(double offset)
    {
        double max = Math.Max(0, _extentSize.Height - _viewportSize.Height);
        double coerced = Math.Clamp(offset, 0, max);
        if (Math.Abs(coerced - _scrollOffset.Y) > double.Epsilon)
        {
            _scrollOffset.Y = coerced;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) =>
        new(HorizontalOffset, VerticalOffset, ViewportWidth, ViewportHeight);

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 120);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 120);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    // ─── Item-container lifecycle ────────────────────────────────────────────

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0)
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }

        // Invalidate cache so the next measure rebuilds the layout from scratch.
        _layout = null;
        InvalidateMeasure();
    }

    private void HookGenerator(ItemsControl? owner)
    {
        if (_genHooked || owner is null) return;
        ItemContainerGenerator? gen = owner.ItemContainerGenerator;
        if (gen is null) return;
        _genHooked = true;
        gen.ItemsChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                _layout = null;
                InvalidateMeasure();
            }
        };
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || _viewportSize.Width <= 0 || _layout is null) return;
        if (index >= _layout.Count) return;

        ItemLayout entry = _layout[index];
        double top = entry.Top;
        double bottom = top + entry.Height;

        if (top < _scrollOffset.Y)
            SetVerticalOffset(top);
        else if (bottom > _scrollOffset.Y + _viewportSize.Height)
            SetVerticalOffset(bottom - _viewportSize.Height);
    }

    // ─── Layout ──────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        availableSize = new Size(
            Math.Max(0, availableSize.Width),
            Math.Max(0, availableSize.Height));

        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        HookGenerator(owner);

        if (double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height))
            return new Size(0, 0);

        int itemCount = owner?.Items.Count ?? 0;

        if (itemCount == 0)
        {
            _viewportSize = availableSize;
            _extentSize = new Size(0, 0);
            _scrollOffset = new Point(0, 0);
            _layout = null;
            ScrollOwner?.InvalidateScrollInfo();
            return availableSize;
        }

        double itemWidth = Math.Max(1, ItemWidth);

        // v0.14.5: 自适应列数 —— 缩略图等比放大填满宽度，放大到 1.5 倍时插入新列缩回。
        // 始终无右侧空白。瀑布流需要扣除列间间隙。
        int columns = ComputeAdaptiveColumns(availableSize.Width, itemWidth, itemCount);
        double effectiveWidth = (availableSize.Width - (columns - 1) * Gap) / columns;
        _columns = columns;
        _effectiveItemWidth = effectiveWidth;

        // v0.15.0: 上报卡片实际显示宽度（物理像素），供缩略图按显示尺寸选档。
        QL.World.ViewModels.Support.GalleryDisplayMetrics.ReportItemWidth(
            effectiveWidth, VisualTreeHelper.GetDpi(this).DpiScaleX);

        // Rebuild layout cache when column count changed or cache dropped.
        if (_layout is null || _layoutColumnCount != columns || _layout.Count != itemCount)
        {
            _layout = BuildLayout(owner!, columns, effectiveWidth);
            _layoutColumnCount = columns;
        }

        double extentHeight = 0;
        for (int i = 0; i < _layout.Count; i++)
            extentHeight = Math.Max(extentHeight, _layout[i].Top + _layout[i].Height);

        _viewportSize = availableSize;
        _extentSize = new Size(availableSize.Width, Math.Max(0, extentHeight));
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();

        GetVisibleRange(itemCount, out int firstIndex, out int lastIndex);

        IItemContainerGenerator? generator = owner?.ItemContainerGenerator;
        if (generator is null)
            return availableSize;

        GeneratorPosition start = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        int realizedThisPass = 0;
        int foreignContainers = 0;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int index = firstIndex; index <= lastIndex; index++, childIndex++)
            {
                UIElement? child = generator.GenerateNext(out bool newlyRealized) as UIElement;
                if (child is null) break;

                DependencyObject? parent = VisualTreeHelper.GetParent(child);
                if (newlyRealized || parent is null)
                {
                    int insertAt = Math.Clamp(childIndex, 0, InternalChildren.Count);
                    if (insertAt >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(insertAt, child);
                    generator.PrepareItemContainer(child);
                }
                else if (!ReferenceEquals(parent, this))
                {
                    foreignContainers++;
                    continue;
                }

                ItemLayout entry = _layout[index];
                child.Measure(new Size(effectiveWidth, entry.Height));
                realizedThisPass++;
            }
        }

        CleanupContainers(firstIndex, lastIndex, generator);

        // Recovery: same as VirtualizingTilePanel — generator may hand back
        // containers parented to the outgoing panel after an ItemsPanel swap.
        if (InternalChildren.Count == 0 && firstIndex <= lastIndex)
        {
            if (foreignContainers > 0)
                generator.RemoveAll();
            if (realizedThisPass == 0 || foreignContainers > 0)
            {
                Dispatcher.BeginInvoke(new Action(InvalidateMeasure),
                    System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double arrangeWidth = (double.IsInfinity(finalSize.Width) || finalSize.Width <= 0)
            ? _viewportSize.Width
            : finalSize.Width;

        if (_layout is null || _layout.Count == 0)
            return finalSize;

        // v0.14.5: 使用 MeasureOverride 计算的自适应列数和尺寸，保证与测量阶段一致。
        int columns = _columns;
        double itemWidth = _effectiveItemWidth;
        if (columns <= 0)
        {
            columns = Math.Max(1, (int)((arrangeWidth + Gap) / (Math.Max(1, ItemWidth) + Gap)));
            itemWidth = (arrangeWidth - (columns - 1) * Gap) / columns;
        }

        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        IItemContainerGenerator? generator = owner?.ItemContainerGenerator;
        if (generator is null) return finalSize;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0 || itemIndex >= _layout.Count) continue;

            ItemLayout entry = _layout[itemIndex];
            double x = entry.Column * (itemWidth + Gap);
            double y = entry.Top - _scrollOffset.Y;

            child.Arrange(new Rect(x, y, itemWidth, entry.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// v0.14.5: 计算自适应列数。缩略图等比放大填满宽度，放大到 1.5 倍原大小时
    /// 插入新列缩回，始终无右侧空白。最小不低于原大小的 75%。
    /// </summary>
    private static int ComputeAdaptiveColumns(double viewportWidth, double itemWidth, int itemCount)
    {
        if (viewportWidth <= 0 || itemWidth <= 0)
            return 1;

        // 基础列数：用原大小能放几列（含间隙）
        int baseColumns = Math.Max(1, (int)Math.Floor((viewportWidth + Gap) / (itemWidth + Gap)));
        // 用基础列数放大填满
        double expandedWidth = (viewportWidth - (baseColumns - 1) * Gap) / baseColumns;

        // 如果放大超过 1.5 倍，尝试加一列
        const double maxScale = 1.5;
        int columns = baseColumns;
        while (expandedWidth > itemWidth * maxScale && columns < itemCount)
        {
            columns++;
            expandedWidth = (viewportWidth - (columns - 1) * Gap) / columns;
        }

        // 如果加列后缩太小（低于 75%），回退到前一列
        const double minScale = 0.75;
        if (columns > 1 && expandedWidth < itemWidth * minScale)
            columns--;

        return Math.Clamp(columns, 1, Math.Max(1, itemCount));
    }

    private static List<ItemLayout> BuildLayout(ItemsControl owner, int columns, double itemWidth)
    {
        var layout = new List<ItemLayout>(owner.Items.Count);
        var columnTops = new double[columns];
        for (int i = 0; i < owner.Items.Count; i++)
        {
            double aspectRatio = owner.Items[i] is GalleryItemViewModel g
                ? g.AspectRatio
                : 1.0;
            // AspectRatio is already clamped to [0.75, 1.5] upstream; cap min height
            // so tiny thumbnails (e.g. 80px) remain visible at narrow column widths.
            double height = Math.Max(80, itemWidth * aspectRatio);

            int col = 0;
            double minTop = columnTops[0];
            for (int c = 1; c < columns; c++)
            {
                if (columnTops[c] < minTop)
                {
                    minTop = columnTops[c];
                    col = c;
                }
            }

            layout.Add(new ItemLayout(minTop, height, col));
            columnTops[col] = minTop + height + Gap;
        }
        return layout;
    }

    private void GetVisibleRange(int itemCount, out int firstIndex, out int lastIndex)
    {
        if (_layout is null || _layout.Count == 0)
        {
            firstIndex = 0;
            lastIndex = -1;
            return;
        }

        double viewTop = _scrollOffset.Y - Gap;
        double viewBottom = _scrollOffset.Y + _viewportSize.Height + Gap;

        firstIndex = 0;
        for (int i = 0; i < _layout.Count; i++)
        {
            if (_layout[i].Top + _layout[i].Height >= viewTop)
            {
                firstIndex = i;
                break;
            }
        }

        lastIndex = firstIndex - 1;
        for (int i = firstIndex; i < _layout.Count; i++)
        {
            if (_layout[i].Top > viewBottom)
                break;
            lastIndex = i;
        }

        lastIndex = Math.Min(lastIndex, itemCount - 1);
    }

    private void CleanupContainers(
        int firstIndex, int lastIndex, IItemContainerGenerator generator)
    {
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < 0 || itemIndex < firstIndex || itemIndex > lastIndex)
            {
                if (itemIndex >= 0)
                    generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void CoerceOffset()
    {
        double maxY = Math.Max(0, _extentSize.Height - _viewportSize.Height);
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y, 0, maxY);
        _scrollOffset.X = 0;
    }
}
