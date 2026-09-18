using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Views.Support;

/// <summary>
/// Hooks ListBoxItem realize/unload cycles to GalleryThumbnailProvider so
/// only visible rows request/decode thumbnails (pairs with container
/// recycling). Usage: support:VirtualizedThumbnailLoader.Enabled="True".
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
            {
                GetProvider(list)?.ClearRealized();
            }
            ScheduleHook(list);
        };
        list.Loaded += (_, _) => ScheduleHook(list);
    }

    private static void ScheduleHook(ListBox list) =>
        list.Dispatcher.BeginInvoke(
            new Action(() => HookRealizedContainers(list)),
            System.Windows.Threading.DispatcherPriority.Background);

    private static void HookRealizedContainers(ListBox list)
    {
        GalleryThumbnailProviderBridge? provider = GetProvider(list);
        if (provider is null) return;

        ItemContainerGenerator generator = list.ItemContainerGenerator;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;
            if ((bool)container.GetValue(IsHookedProperty))
                continue;

            container.SetValue(IsHookedProperty, true);
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

            if (container.IsLoaded &&
                container.DataContext is GalleryItemViewModel current)
            {
                provider.Attach(current);
            }
        }
    }

    private static GalleryThumbnailProviderBridge? GetProvider(ListBox list) =>
        list.DataContext is GalleryViewModel gallery
            ? new GalleryThumbnailProviderBridge(gallery.Thumbnails)
            : null;
}

/// <summary>Small indirection keeps the support namespace free of VM internals.</summary>
internal sealed class GalleryThumbnailProviderBridge(GalleryThumbnailProvider provider)
{
    public void Attach(GalleryItemViewModel item) => provider.Attach(item);
    public void Detach(GalleryItemViewModel item) => provider.Detach(item);
    public void ClearRealized() => provider.ClearRealized();
}
