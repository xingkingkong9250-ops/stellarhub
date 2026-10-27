using System.Windows;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// 0.17.0 PEND-002: W9 素材组织窗口。把原 B1-2/3/4 暂隐藏的三块入口
/// （收藏夹 / 高频标签 / 贴图组）集中到独立窗口，复用 ShellViewModel 的
/// 树数据与命令，选中条目直接影响主窗口画廊筛选。
/// </summary>
public partial class MaterialOrganizerWindow : Window
{
    public MaterialOrganizerWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        // 打开时刷新一次，避免长时间未操作后计数过期。
        _ = viewModel.CollectionTree.RefreshAsync();
        _ = viewModel.PbrGroupTree.RefreshAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
