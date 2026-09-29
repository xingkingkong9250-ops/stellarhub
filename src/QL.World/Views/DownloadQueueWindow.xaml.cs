using System.Windows;
using QL.World.Services;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// M5-G: download queue window. Listview shows every queue row with live
/// progress + status and per-row Cancel / Retry / Open-Folder actions.
/// </summary>
public partial class DownloadQueueWindow : Window
{
    private readonly DownloadQueueViewModel _viewModel;

    public DownloadQueueWindow(ServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(services);
        InitializeComponent();
        _viewModel = new DownloadQueueViewModel(services.DownloadQueue, services.ImageQuery);
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
