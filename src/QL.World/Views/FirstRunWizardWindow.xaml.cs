using System.Windows;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// W7 首次启动向导。纯展示壳：所有逻辑在 <see cref="FirstRunWizardViewModel"/>；
/// ViewModel 完成（目录已持久化，迁移已执行或已跳过）后以 true 关闭，
/// 用户中途关窗则 DialogResult 为 null（App 据此退出进程）。
/// </summary>
public partial class FirstRunWizardWindow : Window
{
    public FirstRunWizardWindow(FirstRunWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
