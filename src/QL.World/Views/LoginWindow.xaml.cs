using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QL.World.Models;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W5 login window. Hands the PasswordBox.Password to the ViewModel
/// (WPF PasswordBox is not bindable) and closes with DialogResult=true on
/// success. Closing the window (X / Esc) returns non-true, which App.xaml.cs
/// interprets as local-only mode.
///
/// v0.5.0: W5-2 密码框末端显隐开关（PasswordBox 与 TextBox 互换，内容
/// 双向同步，登录始终读 PasswordBox.Password）；W5-5 记录密码启动时
/// 由 ViewModel 取回明文回填一次；W5-6 角落齿轮打开 W3 设置窗口。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;
    private bool _syncingPassword;

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // W5-5: 记录的密码仅在此刻回填到 PasswordBox（明文不走绑定属性）。
        string? rememberedPassword = viewModel.GetRememberedPasswordForPrefill();
        if (!string.IsNullOrEmpty(rememberedPassword))
        {
            PasswordBox.Password = rememberedPassword;
            PasswordPlainBox.Text = rememberedPassword;
        }

        viewModel.LoginSucceeded += user =>
        {
            AuthenticatedUser = user;
            DialogResult = true;
            Close();
        };
    }

    /// <summary>
    /// The authenticated user context (null when the user closed the window
    /// without logging in — local-only mode).
    /// </summary>
    public CurrentUser? AuthenticatedUser { get; private set; }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        // IsBusy 由 ViewModel 控制 CanLogin，从而联动 LoginButton.IsEnabled。
        await _viewModel.LoginAsync(PasswordBox.Password);
        // If login succeeded the window is already closed by the event handler.
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSettingsCommand.Execute(null);
    }

    // ─── W5-2 密码显隐切换 ────────────────────────────────────────────────

    private void OnPasswordVisibleChanged(object sender, RoutedEventArgs e)
    {
        bool showPlain = PasswordVisibilityToggle.IsChecked == true;
        if (showPlain)
        {
            PasswordPlainBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordPlainBox.Visibility = Visibility.Visible;
            PasswordVisibilityToggle.ToolTip = "隐藏密码";
            PasswordPlainBox.Focus();
            PasswordPlainBox.CaretIndex = PasswordPlainBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordPlainBox.Text;
            PasswordPlainBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordVisibilityToggle.ToolTip = "显示密码";
            PasswordBox.Focus();
            Keyboard.Focus(PasswordBox);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        // 掩码态输入时保持明文副本同步（切换瞬间无需补同步）。
        if (_syncingPassword) return;
        _syncingPassword = true;
        try
        {
            PasswordPlainBox.Text = PasswordBox.Password;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void OnPasswordPlainTextChanged(object sender, TextChangedEventArgs e)
    {
        // 明文态输入时回写 PasswordBox——登录始终读 PasswordBox.Password。
        if (_syncingPassword) return;
        _syncingPassword = true;
        try
        {
            PasswordBox.Password = PasswordPlainBox.Text;
        }
        finally
        {
            _syncingPassword = false;
        }
    }
}
