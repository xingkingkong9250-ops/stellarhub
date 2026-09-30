using System.Windows;
using QL.World.Services.Nas;
using QL.World.ViewModels;

namespace QL.World.Views;

/// <summary>
/// U6-C (W6): admin review queue window. Lists pending submissions with
/// thumbnails and per-row 通过/拒绝 actions.
/// </summary>
public partial class ReviewQueueWindow : Window
{
    private readonly ReviewQueueViewModel _viewModel;

    public ReviewQueueWindow(
        SubmissionService submissions,
        SharedThumbnailCache thumbnails,
        string currentUsername)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        ArgumentNullException.ThrowIfNull(thumbnails);
        InitializeComponent();
        _viewModel = new ReviewQueueViewModel(submissions, thumbnails, currentUsername);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }
}
