using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Views.Support;

/// <summary>
/// Hooks ListBoxItem realize/unload cycles to GalleryThumbnailProvider so
/// only visible rows request/decode thumbnails (pairs with container
/// recycling). Usage: support:VirtualizedThumbnailLoader.Enabled="True".
///
/// F-15: realization must be reconciled from the LIVE VISUAL TREE, not from
/// ItemContainerGenerator.ContainerFromIndex. The custom
/// <see cref="VirtualizingTilePanel"/> generates containers during its
/// measure pass on scroll; those scroll-realized containers are not reported
/// by ContainerFromIndex at Background priority, and — because the panel
/// removes/re-adds children directly — the containers do not reliably raise
/// Loaded/DataContextChanged. The old generator-index poll therefore hooked
/// only the first screenful; every card scrolled into view stayed black
/// forever. We now:
///   1. listen to ScrollViewer.ScrollChanged (it bubbles up from the
///      ListBox's internal ScrollViewer) in addition to ItemsChanged/Loaded;
///   2. on every trigger, walk the visual tree for the ListBoxItems that
///      actually exist, hook each once, and Attach its CURRENT DataContext
///      (Attach is idempotent — a cache hit returns immediately), so a
///      recycled container whose DataContext swapped without an event is
///      still reconciled;
///   3. keep Unloaded → Detach (that event does fire) to release off-screen
///      rows, plus Loaded/DataContextChanged as fast paths.
/// </summary>
public static class VirtualizedThumbnailLoader
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(VirtualizedThumbnailLoader),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked",
            typeof(bool),
            typeof(VirtualizedThumbnailLoader),
            new PropertyMetadata(false));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || !(bool)e.NewValue) return;

        list.ItemContainerGenerator.ItemsChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                GetProvider(list)?.ClearRealized();
            ScheduleReconcile(list);
        };
        list.Loaded += (_, _) => ScheduleReconcile(list);

        // ScrollChanged bubbles from the internal ScrollViewer; this is the
        // signal that the virtualizing panel has just realized a new slice of
        // containers for the scrolled-into-view rows.
        list.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => ScheduleReconcile(list)));
    }

    private static void ScheduleReconcile(ListBox list) =>
        list.Dispatcher.BeginInvoke(
            new Action(() => Reconcile(list)),
            DispatcherPriority.Background);

    private static void Reconcile(ListBox list)
    {
        IGalleryThumbnailProvider? provider = GetProvider(list);
        if (provider is null)
            return;

        // Ground truth: the containers currently in the live visual tree.
        var containers = new List<ListBoxItem>();
        CollectListBoxItems(list, containers);

        foreach (ListBoxItem container in containers)
        {
            if (!(bool)container.GetValue(IsHookedProperty))
            {
                container.SetValue(IsHookedProperty, true);
                HookContainer(container, provider);
            }

            // Attach the current row regardless of hook state: a recycled
            // container can present a new DataContext without raising
            // DataContextChanged, and a scroll-realized container may already
            // be loaded by the time we walk it. Attach is idempotent.
            if (container.IsLoaded &&
                container.DataContext is GalleryItemViewModel current)
            {
                provider.Attach(current);
            }
        }
    }

    private static void HookContainer(ListBoxItem container, IGalleryThumbnailProvider provider)
    {
        container.Loaded += (_, _) =>
        {
            if (container.DataContext is GalleryItemViewModel item)
                provider.Attach(item);
        };
        container.Unloaded += (_, _) =>
        {
            if (container.DataContext is GalleryItemViewModel item)
                provider.Detach(item);
        };
        // Fast path for recycling swaps; Reconcile is the safety net when this
        // event does not fire. Detach is identity-checked in the provider, so
        // the ordering of this event vs Unloaded can never evict the new row.
        container.DataContextChanged += (_, args) =>
        {
            if (args.OldValue is GalleryItemViewModel oldItem)
                provider.Detach(oldItem);
            if (args.NewValue is GalleryItemViewModel newItem)
                provider.Attach(newItem);
        };
    }

    private static void CollectListBoxItems(DependencyObject root, List<ListBoxItem> acc)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBoxItem item)
                acc.Add(item);
            else
                CollectListBoxItems(child, acc);
        }
    }

    private static IGalleryThumbnailProvider? GetProvider(ListBox list) =>
        list.DataContext switch
        {
            GalleryViewModel gallery => gallery.Thumbnails,
            SharedGalleryViewModel shared => shared.Thumbnails,
            _ => null,
        };
}
