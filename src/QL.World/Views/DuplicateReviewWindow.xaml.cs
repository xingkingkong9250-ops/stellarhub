using System.Windows;
using QL.World.Services;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// 0.16.0 DUP-D (W8): 重复审核窗口。左侧重复组列表，右侧组内图片详情，
/// 支持全库扫描（进度/取消）、类型与状态筛选、保留全部、删除选中。
/// </summary>
public partial class DuplicateReviewWindow : Window
{
    private readonly DuplicateReviewViewModel _viewModel;

    public DuplicateReviewWindow(ServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(services);
        InitializeComponent();
        _viewModel = new DuplicateReviewViewModel(
            services.DuplicateReview,
            services.Duplicates,
            services.ImageQuery,
            services.Thumbnails);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }
}
