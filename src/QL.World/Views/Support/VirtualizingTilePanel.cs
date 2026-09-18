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
/// Based on the classic VirtualizingTilePanel pattern (IScrollInfo).
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

    // ─── Layout ──────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // During the initial HwndSource setup (PerMonitorV2) WPF may pass
        // tiny negative constraints; IScrollInfo must never report those.
        availableSize = new Size(
            Math.Max(0, availableSize.Width),
            Math.Max(0, availableSize.Height));

        ItemsControl? owner = ItemsControl.GetItemsOwner(this);

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
        int columns = Math.Max(1, (int)(availableSize.Width / itemWidth));
        int rows = (int)Math.Ceiling((double)itemCount / columns);

        _viewportSize = availableSize;
        _extentSize = new Size(availableSize.Width, Math.Max(0, rows * itemHeight));
        CoerceOffset();
        ScrollOwner?.InvalidateScrollInfo();

        GetVisibleRange(columns, rows, itemCount, out int firstIndex, out int lastIndex);

        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition start = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int index = firstIndex; index <= lastIndex; index++, childIndex++)
            {
                UIElement child = (UIElement)generator.GenerateNext(out bool newlyRealized);
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(Math.Max(0, childIndex), child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(itemWidth, itemHeight));
            }
        }

        CleanupContainers(firstIndex, lastIndex);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double itemWidth = ItemWidth;
        double itemHeight = ItemHeight;
        int columns = Math.Max(1, (int)(finalSize.Width / itemWidth));
        IItemContainerGenerator generator = ItemContainerGenerator;

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
        int firstRow = Math.Max(0, (int)Math.Floor(_scrollOffset.Y / ItemHeight) - 1);
        int visibleRowCount = (int)Math.Ceiling(_viewportSize.Height / ItemHeight) + 2;
        int lastRow = Math.Min(totalRows - 1, firstRow + visibleRowCount);

        firstIndex = firstRow * columns;
        lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
    }

    private void CleanupContainers(int firstIndex, int lastIndex)
    {
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(
                new GeneratorPosition(i, 0));
            if (itemIndex >= 0 && (itemIndex < firstIndex || itemIndex > lastIndex))
                ItemContainerGenerator.Remove(new GeneratorPosition(i, 0), 1);
        }
    }

    private void CoerceOffset()
    {
        double maxY = Math.Max(0, _extentSize.Height - _viewportSize.Height);
        _scrollOffset.Y = Math.Clamp(_scrollOffset.Y, 0, maxY);
        _scrollOffset.X = 0;
    }
}
