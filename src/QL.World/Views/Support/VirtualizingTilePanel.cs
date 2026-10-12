using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QL.World.Views.Support;

/// <summary>
/// Pixel-scrolling virtualizing wrap panel for the B2 grid.
/// Realizes only the containers intersecting the vertical viewport and
/// recycles them via the ItemContainerGenerator (container recycling must
/// be enabled on the owning ListBox).
/// Based on the classic VirtualizingTilePanel pattern (IScrollInfo), with
/// the container-lifecycle gaps that caused F-13 closed:
/// ・out-of-range containers are detached with RemoveInternalChildRange —
///   IItemContainerGenerator.Remove only updates generator bookkeeping and
///   never removes the visual, so skipping it left orphan cards stacked in
///   the panel (ghost overlap / misplacement / blank slots);
/// ・OnItemsChanged reconciles Add/Remove/Replace/Move/Reset;
/// ・a generator-reported realized child that is not visually parented to
///   this panel (right after an ItemsPanel swap on Grid↔List switch) is
///   re-attached instead of silently skipped.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _viewportSize;
    private Size _extentSize;
    private Point _scrollOffset;

    // v0.14.5: 自适应布局缓存。窗口放大时缩略图等比放大填满宽度，
    // 放大到 1.5 倍时插入新列缩回，始终无右侧空白。
    private int _columns;
    private double _effectiveItemWidth;
    private double _effectiveItemHeight;

    // F-14: the base VirtualizingPanel.OnItemsChanged path is unreliable
    // when the panel is created before items arrive (e.g. Grid mode at
    // startup with async data loading). The generator's ItemsChanged event
    // may not reach the virtual OnItemsChanged because the ItemsPresenter's
    // hookup races with the first measure pass. Subscribe directly to the
    // generator's event on Loaded as a safety net.
    private bool _genHooked;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
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

    public void SetHorizontalOffset(double offset) { /* grid never scrolls horizontally */ }

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

        // Classic VirtualizingTilePanel contract: only Remove/Replace/Move
        // carry a visual range the panel must detach itself. A Reset is owned
        // by the ItemContainerGenerator — it re-links recycled containers on
        // the next measure; manually dropping InternalChildren here races with
        // generator bookkeeping and leaves the viewport permanently blank
        // (F-13 regression observed after an explicit Items.Refresh).
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0)
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }

        // F-14: VirtualizingPanel.OnItemsChanged does NOT call base
        // (Panel.OnItemsChanged), so InvalidateMeasure is never invoked
        // after items arrive. Without this, a panel that first measured
        // with 0 items (e.g. created before RefreshAsync loaded data)
        // never re-measures and stays permanently blank.
        InvalidateMeasure();
    }

    private void HookGenerator(ItemsControl? owner)
    {
        if (_genHooked || owner is null)
            return;
        ItemContainerGenerator? gen = owner.ItemContainerGenerator;
        if (gen is null)
            return;
        _genHooked = true;
        gen.ItemsChanged += (_, args) =>
        {
            // F-14: the base virtual OnItemsChanged path is unreliable when
            // the panel measures before items arrive. Re-invalidate here so
            // the next measure pass re-generates containers for the new items.
            if (args.Action == NotifyCollectionChangedAction.Reset)
                InvalidateMeasure();
        };
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || _viewportSize.Width <= 0)
            return;

        int columns = Math.Max(1, (int)(_viewportSize.Width / Math.Max(1, ItemWidth)));
        double itemHeight = Math.Max(1, ItemHeight);
        int row = index / columns;
        double top = row * itemHeight;
        double bottom = top + itemHeight;

        if (top < _scrollOffset.Y)
            SetVerticalOffset(top);
        else if (bottom > _scrollOffset.Y + _viewportSize.Height)
            SetVerticalOffset(bottom - _viewportSize.Height);
    }

    // ─── Layout ──────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // During the initial HwndSource setup (PerMonitorV2) WPF may pass
        // tiny negative constraints; IScrollInfo must never report those.
        availableSize = new Size(
            Math.Max(0, availableSize.Width),
            Math.Max(0, availableSize.Height));

        ItemsControl? owner = ItemsControl.GetItemsOwner(this);

        // F-14: hook the generator's ItemsChanged event directly as a safety
        // net. The base VirtualizingPanel hookup can race with async data
        // loading — the panel may measure with 0 items before the generator
        // connection is fully established, and then never receive the Reset
        // notification when items arrive.
        HookGenerator(owner);

        if (double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height))
            return new Size(0, 0);

        int itemCount = owner?.Items.Count ?? 0;

        if (itemCount == 0)
        {
            _viewportSize = availableSize;
            // NOTE: Size.Empty holds (NegativeInfinity, NegativeInfinity) and
            // must never be reported through IScrollInfo — ScrollViewer builds
            // a Size from these values and throws on negatives during layout.
            _extentSize = new Size(0, 0);
            _scrollOffset = new Point(0, 0);
            ScrollOwner?.InvalidateScrollInfo();
            return availableSize;
        }

        double itemWidth = Math.Max(1, ItemWidth);
        double itemHeight = Math.Max(1, ItemHeight);

        // v0.14.5: 自适应列数 —— 缩略图等比放大填满宽度，放大到 1.5 倍时插入新列缩回。
        // 始终无右侧空白。
        int columns = ComputeAdaptiveColumns(availableSize.Width, itemWidth, itemCount);
        double effectiveWidth = availableSize.Width / columns;
        double effectiveHeight = effectiveWidth / itemWidth * itemHeight;
        _columns = columns;
        _effectiveItemWidth = effectiveWidth;
        _effectiveItemHeight = effectiveHeight;

        int rows = (int)Math.Ceiling((double)itemCount / columns);

        _viewportSize = availableSize;
        _extentSize = new Size(availableSize.Width, Math.Max(0, rows * effectiveHeight));
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();

        GetVisibleRange(columns, rows, itemCount, out int firstIndex, out int lastIndex);

        // During an ItemsPanel swap (Grid↔List view-mode switch) a pending
        // measure can still reach this panel after WPF has unregistered it as
        // the ItemsHost; ItemContainerGenerator is null then and must not be
        // touched (NRE crash on view-mode switch).
        // NOTE: .NET 10's VirtualizingPanel.ItemContainerGenerator property can
        // return null even when the panel is the ItemsHost and the owning
        // ItemsControl has a live ItemContainerGenerator (observed in .NET 10
        // desktop apps). Resolve the generator from the owner directly so we
        // never silently return without realizing containers — that left the
        // gallery permanently blank in Local-only mode.
        IItemContainerGenerator? generator = owner?.ItemContainerGenerator;
        if (generator is null)
        {
            return availableSize;
        }

        GeneratorPosition start = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        int realizedThisPass = 0;
        int foreignContainers = 0;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int index = firstIndex; index <= lastIndex; index++, childIndex++)
            {
                UIElement? child = generator.GenerateNext(out bool newlyRealized) as UIElement;
                if (child is null)
                    break;

                // F-13/F-14: right after an ItemsPanel swap (Grid↔List) the
                // generator can hand back containers it still considers
                // realized. Two sub-states:
                // ・orphan  — no visual parent: safe to re-attach here;
                // ・foreign — still parented to the OUTGOING panel: it can
                //   never render in this panel (AddInternalChild on an
                //   already-parented element throws), so skip it and let the
                //   recovery below reset the generator. The old check only
                //   treated "no parent" as attachable, which left the whole
                //   viewport blank after a swap.
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

                child.Measure(new Size(effectiveWidth, effectiveHeight));
                realizedThisPass++;
            }
        }

        CleanupContainers(firstIndex, lastIndex, generator);

        // F-13/F-14 recovery: data exists and the visible range is non-empty,
        // yet the panel holds no containers.
        // ・realizedThisPass == 0 — the generator produced nothing this pass
        //   (swap / reset timing): measure again at Render priority;
        // ・foreignContainers > 0 — every container handed back still belongs
        //   to the outgoing panel: drop the generator's stale bookkeeping
        //   with RemoveAll() so the next pass generates fresh (or detached
        //   recycled) containers, then measure again.
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
        // Mirror of the MeasureOverride unbounded guard: a pending arrange can
        // still reach this panel with an infinite or non-positive width (e.g.
        // the outgoing panel draining its final layout pass during an
        // ItemsPanel swap). Fall back to the last measured viewport so the
        // column math below never casts infinity/NaN into the column count.
        double arrangeWidth = (double.IsInfinity(finalSize.Width) || finalSize.Width <= 0)
            ? _viewportSize.Width
            : finalSize.Width;

        // v0.14.5: 使用 MeasureOverride 计算的自适应列数和尺寸，保证与测量阶段一致。
        int columns = _columns;
        double itemWidth = _effectiveItemWidth;
        double itemHeight = _effectiveItemHeight;
        if (columns <= 0)
        {
            columns = Math.Max(1, (int)(arrangeWidth / Math.Max(1, ItemWidth)));
            itemWidth = arrangeWidth / columns;
            itemHeight = itemWidth / Math.Max(1, ItemWidth) * Math.Max(1, ItemHeight);
        }

        // Same teardown guard as MeasureOverride: the generator is gone while
        // the outgoing panel drains its final layout pass. Resolve from owner
        // (see MeasureOverride note on .NET 10 VirtualizingPanel behavior).
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        IItemContainerGenerator? generator = owner?.ItemContainerGenerator;
        if (generator is null)
            return finalSize;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;

            int column = itemIndex % columns;
            int row = itemIndex / columns;

            child.Arrange(new Rect(
                column * itemWidth - _scrollOffset.X,
                row * itemHeight - _scrollOffset.Y,
                itemWidth,
                itemHeight));
        }

        return finalSize;
    }

    private void GetVisibleRange(
        int columns, int totalRows, int itemCount,
        out int firstIndex, out int lastIndex)
    {
        double effectiveHeight = _effectiveItemHeight > 0 ? _effectiveItemHeight : Math.Max(1, ItemHeight);
        int firstRow = Math.Max(0, (int)Math.Floor(_scrollOffset.Y / effectiveHeight) - 1);
        int visibleRowCount = (int)Math.Ceiling(_viewportSize.Height / effectiveHeight) + 2;
        int lastRow = Math.Min(totalRows - 1, firstRow + visibleRowCount);

        firstIndex = firstRow * columns;
        lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
    }

    /// <summary>
    /// v0.14.5: 计算自适应列数。缩略图等比放大填满宽度，放大到 1.5 倍原大小时
    /// 插入新列缩回，始终无右侧空白。最小不低于原大小的 75%。
    /// </summary>
    private static int ComputeAdaptiveColumns(double viewportWidth, double itemWidth, int itemCount)
    {
        if (viewportWidth <= 0 || itemWidth <= 0)
            return 1;

        // 基础列数：用原大小能放几列
        int baseColumns = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        // 用基础列数放大填满
        double expandedWidth = viewportWidth / baseColumns;

        // 如果放大超过 1.5 倍，尝试加一列
        const double maxScale = 1.5;
        int columns = baseColumns;
        while (expandedWidth > itemWidth * maxScale && columns < itemCount)
        {
            columns++;
            expandedWidth = viewportWidth / columns;
        }

        // 如果加列后缩太小（低于 75%），回退到前一列
        const double minScale = 0.75;
        if (columns > 1 && expandedWidth < itemWidth * minScale)
            columns--;

        return Math.Clamp(columns, 1, Math.Max(1, itemCount));
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
                // F-13: generator.Remove updates generation bookkeeping ONLY;
                // the visual container stays in InternalChildren until the
                // panel detaches it. Orphaned (-1) visuals are already
                // detached from the generator — just drop the visual.
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
