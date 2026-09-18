using System.Windows;
using System.Windows.Controls;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W3 settings window (A1 常规 / A2 缩略图 / A3 AI / A4 监控 / A5 关于).
/// ViewModel changes persist immediately; the close button just closes.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.RequestClose += Close;
        Closed += (_, _) => viewModel.RequestClose -= Close;
    }

    /// <summary>
    /// PasswordBox cannot bind its Password safely through data binding.
    /// Forward changes directly to the ViewModel which encrypts and persists
    /// via DPAPI — the plaintext never lives in a bindable property.
    /// </summary>
    private void OnCloudApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            _viewModel.SetCloudApiKeyInput(pb.Password);
    }
}
