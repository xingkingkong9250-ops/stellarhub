using System.ComponentModel;
using System.Windows;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W2 import dialog. Shows a modal progress surface; finish/close is
/// driven by ImportViewModel.RequestClose.
/// </summary>
public partial class ImportDialog : Window
{
    private readonly ImportViewModel _viewModel;

    public ImportDialog(ImportViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(int addedCount)
    {
        DialogResult = addedCount > 0;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Keep the dialog open while the pipeline is still running;
        // cancellation happens through the 取消导入 button.
        if (_viewModel.IsRunning)
            e.Cancel = true;

        _viewModel.RequestClose -= OnRequestClose;
        base.OnClosing(e);
    }
}
