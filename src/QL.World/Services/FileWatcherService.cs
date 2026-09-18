using System.Collections.ObjectModel;
using System.IO;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// Wraps a single directory watcher for testability.
/// Production implementation uses FileSystemWatcher.
/// </summary>
public interface IFileWatcher : IDisposable
{
    string Directory { get; }
    void Start();
    void Stop();
    event Action<IReadOnlyList<string>>? FilesCreated;
    event Action<IReadOnlyList<string>>? FilesDeleted;
    event Action<IReadOnlyList<(string OldPath, string NewPath)>>? FilesRenamed;
}

/// <summary>
/// Monitors configured directories for new/deleted/renamed image files.
/// Events are debounced (2s) and batch-processed: new files trigger
/// auto-import (if enabled), deleted files are marked is_missing,
/// and results are published to NotificationService.
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private const int DEBOUNCE_MS = 2000;

    private readonly AppSettingsService _settings;
    private readonly ImportService _import;
    private readonly ImageQueryService _query;
    private readonly NotificationService _notifications;
    private readonly Action<string>? _log;
    private readonly List<IFileWatcher> _watchers = [];
    private System.Threading.Timer? _debounceTimer;
    private readonly List<string> _pendingCreated = [];
    private readonly List<string> _pendingDeleted = [];
    private readonly List<(string OldPath, string NewPath)> _pendingRenamed = [];
    private bool _disposed;

    public FileWatcherService(
        AppSettingsService settings,
        ImportService import,
        ImageQueryService query,
        NotificationService notifications,
        Action<string>? log = null)
    {
        _settings = settings;
        _import = import;
        _query = query;
        _notifications = notifications;
        _log = log;
    }

    public void Start()
    {
        UpdateDirectories(_settings.WatcherDirectories);
    }

    public void Stop()
    {
        foreach (IFileWatcher watcher in _watchers)
            watcher.Stop();
        FlushPending();
    }

    /// <summary>Diffs the current watchers against the new directory list.</summary>
    public void UpdateDirectories(IReadOnlyList<string> directories)
    {
        var normalized = directories
            .Where(Directory.Exists)
            .Select(d => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Stop and dispose watchers for removed directories.
        var toRemove = _watchers
            .Where(w => !normalized.Any(d =>
                string.Equals(d, w.Directory, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (IFileWatcher watcher in toRemove)
        {
            watcher.Stop();
            watcher.Dispose();
            _watchers.Remove(watcher);
        }

        // Create watchers for new directories.
        foreach (string dir in normalized)
        {
            if (_watchers.Any(w =>
                string.Equals(w.Directory, dir, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var watcher = new FileSystemWatcherAdapter(dir);
                watcher.FilesCreated += OnCreated;
                watcher.FilesDeleted += OnDeleted;
                watcher.FilesRenamed += OnRenamed;
                watcher.Start();
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _notifications.Publish(NotificationKind.WatcherDirectoryError,
                    "监控目录错误", $"无法监控目录 {dir}: {ex.Message}");
                _log?.Invoke($"[WATCHER] Failed to watch {dir}: {ex.Message}");
            }
        }
    }

    private void OnCreated(IReadOnlyList<string> paths)
    {
        lock (_pendingCreated)
        {
            _pendingCreated.AddRange(paths);
            ScheduleFlush();
        }
    }

    private void OnDeleted(IReadOnlyList<string> paths)
    {
        lock (_pendingDeleted)
        {
            _pendingDeleted.AddRange(paths);
            ScheduleFlush();
        }
    }

    private void OnRenamed(IReadOnlyList<(string OldPath, string NewPath)> renames)
    {
        lock (_pendingRenamed)
        {
            _pendingRenamed.AddRange(renames);
            ScheduleFlush();
        }
    }

    private void ScheduleFlush()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            _ => FlushPending(), null, DEBOUNCE_MS, Timeout.Infinite);
    }

    private void FlushPending()
    {
        List<string> created;
        List<string> deleted;
        List<(string OldPath, string NewPath)> renamed;

        lock (_pendingCreated)
        {
            created = [.. _pendingCreated];
            _pendingCreated.Clear();
        }
        lock (_pendingDeleted)
        {
            deleted = [.. _pendingDeleted];
            _pendingDeleted.Clear();
        }
        lock (_pendingRenamed)
        {
            renamed = [.. _pendingRenamed];
            _pendingRenamed.Clear();
        }

        if (created.Count > 0)
            ProcessCreated(created);
        if (deleted.Count > 0)
            ProcessDeleted(deleted);
        if (renamed.Count > 0)
            ProcessRenamed(renamed);
    }

    private void ProcessCreated(List<string> paths)
    {
        var imagePaths = paths
            .Where(p => ImportOptions.SupportedExtensions.Contains(
                Path.GetExtension(p).ToLowerInvariant()))
            .ToList();
        if (imagePaths.Count == 0) return;

        if (!_settings.AutoImportOnWatch) return;

        try
        {
            var options = new ImportOptions
            {
                InputPaths = imagePaths,
                RecurseSubfolders = false,
                SkipHidden = true,
                EnabledExtensions = ImportOptions.SupportedExtensions,
            };
            ImportReport report = _import.ImportAsync(options, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (report.AddedCount > 0)
                _notifications.Publish(NotificationKind.WatcherImported,
                    "监控导入", $"自动导入 {report.AddedCount} 张新图片");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WATCHER] Auto-import failed: {ex.Message}");
        }
    }

    private void ProcessDeleted(List<string> paths)
    {
        var missingPaths = new List<string>();
        foreach (string path in paths)
        {
            long? id = _query.FindByPath(path);
            if (id is { } imageId)
            {
                _query.SetMissing(imageId, isMissing: true);
                missingPaths.Add(path);
            }
        }

        if (missingPaths.Count > 0)
            _notifications.PublishBatch(NotificationKind.FilesMissing,
                "文件缺失", missingPaths);
    }

    private void ProcessRenamed(List<(string OldPath, string NewPath)> renames)
    {
        var deletedPaths = new List<string>();
        var createdPaths = new List<string>();

        foreach ((string oldPath, string newPath) in renames)
        {
            long? id = _query.FindByPath(oldPath);
            if (id is not null)
            {
                _query.SetMissing(id.Value, isMissing: true);
                deletedPaths.Add(oldPath);
            }

            if (ImportOptions.SupportedExtensions.Contains(
                Path.GetExtension(newPath).ToLowerInvariant()))
                createdPaths.Add(newPath);
        }

        if (createdPaths.Count > 0 && _settings.AutoImportOnWatch)
            ProcessCreated(createdPaths);
        if (deletedPaths.Count > 0)
            _notifications.PublishBatch(NotificationKind.FilesMissing,
                "文件缺失（重命名）", deletedPaths);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer?.Dispose();
        foreach (IFileWatcher watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}

/// <summary>Production FileSystemWatcher adapter.</summary>
internal sealed class FileSystemWatcherAdapter : IFileWatcher
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemWatcherAdapter(string directory)
    {
        Directory = directory;
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                          | NotifyFilters.LastWrite,
        };
    }

    public string Directory { get; }

    public event Action<IReadOnlyList<string>>? FilesCreated;
    public event Action<IReadOnlyList<string>>? FilesDeleted;
    public event Action<IReadOnlyList<(string OldPath, string NewPath)>>? FilesRenamed;

    public void Start()
    {
        _watcher.Created += (s, e) =>
            FilesCreated?.Invoke([e.FullPath]);
        _watcher.Deleted += (s, e) =>
            FilesDeleted?.Invoke([e.FullPath]);
        _watcher.Renamed += (s, e) =>
            FilesRenamed?.Invoke([(e.OldFullPath, e.FullPath)]);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop() => _watcher.EnableRaisingEvents = false;

    public void Dispose() => _watcher.Dispose();
}
