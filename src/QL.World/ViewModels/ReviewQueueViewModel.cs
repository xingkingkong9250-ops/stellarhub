using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>
/// U6-C: backs the W6 review queue window. Loads every pending submission,
/// fetches its thumbnail, and runs admin Approve / Reject (which relocate
/// files on the NAS) removing the row on success.
/// </summary>
public sealed class ReviewQueueViewModel : ObservableObject
{
    private readonly SubmissionService _submissions;
    private readonly SharedThumbnailCache _thumbnails;
    private readonly string _currentUsername;

    public ReviewQueueViewModel(
        SubmissionService submissions,
        SharedThumbnailCache thumbnails,
        string currentUsername)
    {
        _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _currentUsername = currentUsername ?? throw new ArgumentNullException(nameof(currentUsername));

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ApproveCommand = new AsyncRelayCommand<object?>(ApproveAsync);
        RejectCommand = new AsyncRelayCommand<object?>(RejectAsync);
    }

    public ObservableCollection<ReviewItemViewModel> Items { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<object?> ApproveCommand { get; }
    public AsyncRelayCommand<object?> RejectCommand { get; }

    /// <summary>Raised whenever the pending count may have changed (shell badge refresh).</summary>
    public event Action? QueueChanged;

    public async Task LoadAsync()
    {
        var pending = await _submissions
            .ListPendingAsync(CancellationToken.None)
            .ConfigureAwait(true);

        Items.Clear();
        foreach (var sub in pending)
        {
            var item = new ReviewItemViewModel(sub);
            Items.Add(item);
            _ = LoadThumbnailAsync(item, sub);
        }
        QueueChanged?.Invoke();
    }

    private async Task LoadThumbnailAsync(ReviewItemViewModel item, PendingSubmission sub)
    {
        item.Thumbnail = await _thumbnails
            .EnsurePendingAsync(sub.FileHash, sub.SubmitId)
            .ConfigureAwait(true);
    }

    private async Task ApproveAsync(object? parameter)
    {
        if (parameter is not ReviewItemViewModel item) return;

        await _submissions
            .ApproveAsync(item.Source, _currentUsername, CancellationToken.None)
            .ConfigureAwait(true);

        Items.Remove(item);
        QueueChanged?.Invoke();
    }

    private async Task RejectAsync(object? parameter)
    {
        if (parameter is not ReviewItemViewModel item) return;

        await _submissions
            .RejectAsync(item.SubmitId, CancellationToken.None)
            .ConfigureAwait(true);

        Items.Remove(item);
        QueueChanged?.Invoke();
    }
}
