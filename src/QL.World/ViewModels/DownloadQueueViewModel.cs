using System.Collections.ObjectModel;
using System.Windows.Input;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// M5-G: bindable wrapper over <see cref="DownloadQueueService"/>. Subscribes
/// to <see cref="DownloadQueueService.Changed"/> and refreshes the in-place
/// item list on every progress / status change.
/// </summary>
public sealed class DownloadQueueViewModel : ObservableObject, IDisposable
{
    private readonly DownloadQueueService _queue;
    private readonly ImageQueryService _query;
    private bool _disposed;

    public DownloadQueueViewModel(DownloadQueueService queue, ImageQueryService query)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(query);
        _queue = queue;
        _query = query;
        _queue.Changed += OnQueueChanged;
        Refresh();

        CancelCommand = new RelayCommand(CancelItem, CanCancelItem);
        RetryCommand = new RelayCommand(RetryItem, CanRetryItem);
        OpenFolderCommand = new RelayCommand(OpenFolder, CanOpenFolder);
    }

    /// <summary>Queue items, newest first (same order as <see cref="DownloadQueueService.GetAll"/>).</summary>
    public ObservableCollection<DownloadQueueItemViewModel> Items { get; } = new();

    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Reloads from the service and syncs the observable collection in place.</summary>
    public void Refresh()
    {
        List<DownloadQueueItem> snapshot = _queue.GetAll();

        // Remove items no longer in the DB.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (snapshot.All(it => it.Id != Items[i].Id))
                Items.RemoveAt(i);
        }

        // Update or insert in DB order so the newest-first ordering matches.
        for (int i = 0; i < snapshot.Count; i++)
        {
            DownloadQueueItem src = snapshot[i];
            if (i < Items.Count && Items[i].Id == src.Id)
            {
                Items[i].UpdateFrom(src);
            }
            else
            {
                // Find existing by id (could be out of order after removals) and move it.
                DownloadQueueItemViewModel? existing = Items.FirstOrDefault(it => it.Id == src.Id);
                if (existing is not null)
                {
                    existing.UpdateFrom(src);
                    int currentIndex = Items.IndexOf(existing);
                    if (currentIndex != i)
                    {
                        Items.Move(currentIndex, i);
                        continue;
                    }
                }
                else
                {
                    Items.Insert(i, new DownloadQueueItemViewModel(src));
                }
            }
        }

        // Truncate trailing leftovers that no longer exist in DB.
        while (Items.Count > snapshot.Count)
            Items.RemoveAt(Items.Count - 1);
    }

    private void OnQueueChanged()
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(Refresh);
    }

    private bool CanCancelItem(object? parameter) =>
        parameter is DownloadQueueItemViewModel vm
        && vm.Status is "pending" or "downloading";

    private void CancelItem(object? parameter)
    {
        if (parameter is DownloadQueueItemViewModel vm)
            _ = _queue.CancelAsync(vm.Id);
    }

    private bool CanRetryItem(object? parameter) =>
        parameter is DownloadQueueItemViewModel vm
        && vm.Status is "failed" or "cancelled";

    private void RetryItem(object? parameter)
    {
        if (parameter is DownloadQueueItemViewModel vm)
            _ = _queue.RetryAsync(vm.Id);
    }

    private bool CanOpenFolder(object? parameter) =>
        parameter is DownloadQueueItemViewModel vm
        && !string.IsNullOrEmpty(vm.LocalTarget);

    private static void OpenFolder(object? parameter)
    {
        if (parameter is DownloadQueueItemViewModel vm)
            SystemNavigator.RevealFile(vm.LocalTarget);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Changed -= OnQueueChanged;
    }
}

/// <summary>
/// One row in the download queue listview. Wraps a <see cref="DownloadQueueItem"/>
/// with INPC so the UI updates live as the worker pushes progress / status.
/// </summary>
public sealed class DownloadQueueItemViewModel : ObservableObject
{
    private readonly DownloadQueueItem _item;
    private string _status;
    private double _progress;
    private string? _error;

    public DownloadQueueItemViewModel(DownloadQueueItem item)
    {
        _item = item;
        _status = item.Status;
        _progress = item.Progress;
        _error = item.Error;
    }

    public long Id => _item.Id;
    public string FileName => _item.FileName;
    public string LocalTarget => _item.LocalTarget;

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                _item.Status = value;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
                _item.Progress = value;
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                _item.Error = value;
        }
    }

    public string StatusText => Status switch
    {
        "pending" => "等待中",
        "downloading" => "下载中",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        _ => Status,
    };

    public bool IsPending => Status == "pending";
    public bool IsDownloading => Status == "downloading";
    public bool IsCompleted => Status == "completed";
    public bool IsFailed => Status == "failed";
    public bool IsCancelled => Status == "cancelled";
    public bool CanRetry => Status is "failed" or "cancelled";
    public bool CanCancel => Status is "pending" or "downloading";

    /// <summary>Push a fresh DB row into this wrapper.</summary>
    public void UpdateFrom(DownloadQueueItem src)
    {
        Status = src.Status;
        Progress = src.Progress;
        Error = src.Error;
    }
}
