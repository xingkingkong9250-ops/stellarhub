using System.Windows;
using QL.World.Models;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// M6-B (W4): login window. Hands the PasswordBox.Password to the ViewModel
/// (WPF PasswordBox is not bindable) and closes with DialogResult=true on
/// success or false for local-only mode.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.LoginSucceeded += user =>
        {
            AuthenticatedUser = user;
            DialogResult = true;
            Close();
        };
        viewModel.LocalModeRequested += () =>
        {
            DialogResult = false;
            Close();
        };
    }

    /// <summary>
    /// The authenticated user context (null when the user chose local-only mode).
    /// </summary>
    public CurrentUser? AuthenticatedUser { get; private set; }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        // IsBusy 由 ViewModel 控制 CanLogin，从而联动 LoginButton.IsEnabled。
        await _viewModel.LoginAsync(PasswordBox.Password);
        // If login succeeded the window is already closed by the event handler.
    }

    private void OnLocalModeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.LocalModeCommand.Execute(null);
    }

    private void OnOpenNasSettingsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenNasSettingsCommand.Execute(null);
    }
}
