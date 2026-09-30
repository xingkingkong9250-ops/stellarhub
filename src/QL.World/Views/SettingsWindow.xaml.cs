using System.Windows;
using System.Windows.Controls;
using QL.World.Services.Nas;
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

    /// <summary>
    /// 协议 ComboBox SelectionChanged 转发到 ViewModel，让 ViewModel 联动
    /// 端口默认值 + 触发 IsWebDavSelected / IsSmbSelected / IsTlsApplicable
    /// 等派生属性通知。spec 是 POCO 无 INPC，必须借由 View 事件桥接，避免
    /// 改造持久化 schema。直接从 ComboBox.SelectedItem 取协议值传入，不依
    /// 赖 SelectedItem 双向绑定 push 到 source 的时序。
    /// </summary>
    private void OnNasProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0) return; // 初始化触发，无前一协议
        if (sender is ComboBox cb && cb.SelectedItem is NasConnectionProtocol p)
            _viewModel.OnNasProtocolChanged(p);
    }

    /// <summary>
    /// v0.2.1: 未登录场景下打开设置窗口时直接定位到 A7 NAS 页签（index 6），
    /// 便于首次启动配置 NAS。仅当 caller 显式调用时生效；普通打开走默认选中。
    /// </summary>
    public void SelectNasTab() => MainTabs.SelectedIndex = 6;
}
