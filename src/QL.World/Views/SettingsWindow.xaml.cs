using System.Windows;
using System.Windows.Controls;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W3 settings window (A1 常规 / A2 缩略图 / A3 AI / A4 监控 / A5 关于 /
/// A6 贴图组 / A7 NAS 连接). ViewModel changes persist immediately; the close
/// button just closes.
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

        // Clear the NAS PasswordBox whenever selection changes (security:
        // never pre-fill — the plaintext is owned only by the user).
        viewModel.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.SelectedNasConnection))
                NasPasswordBox.Password = string.Empty;
        };
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

    /// <summary>
    /// Same DPAPI-via-ViewModel pattern as OnCloudApiKeyChanged, but the NAS
    /// password is keyed by the selected connection id so it survives across
    /// selection changes (each connection has its own stored password).
    /// </summary>
    private void OnNasPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && _viewModel.SelectedNasConnection is { } spec)
            _viewModel.SetNasPasswordInput(spec.Id, pb.Password);
    }
}
