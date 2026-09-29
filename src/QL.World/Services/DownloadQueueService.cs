using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.Services;

/// <summary>
/// M5-G: persistent download queue for NAS→local file transfers with
/// resume + retry. State machine per item:
/// <code>pending → downloading → completed | failed | cancelled</code>
///
/// Design highlights:
/// <list type="bullet">
/// <item>Concurrency capped at 2 via SemaphoreSlim (background tasks).</item>
/// <item>Resume: a <c>.part</c> sidecar file is appended to via
///   <see cref="INasConnector.DownloadRangeAsync"/>; on
///   <see cref="NasConnectionKind.RangeNotSatisfiable"/> the .part is
///   discarded and the download restarts from offset 0 (M3-D lesson:
///   never reuse HttpRequestMessage).</item>
/// <item>Retry: up to 3 attempts per item with exponential backoff 1s/2s/4s.</item>
/// <item>Cancellation: per-item linked CancellationTokenSource; the .part
///   is deleted on cancel.</item>
/// <item><see cref="Changed"/> event fires on every state transition + chunk
///   progress so the UI can refresh.</item>
/// </list>
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private const int MAX_CONCURRENT = 2;
    private const int MAX_ATTEMPTS = 3;
    private const int CHUNK_SIZE = 4 * 1024 * 1024; // 4 MB
    private static readonly int[] BACKOFF_MS = { 1000, 2000, 4000 };

    private readonly DatabaseService _db;
    private readonly INasConnectorFactory _nasFactory;
    private readonly AppSettingsService _settings;
    private readonly IUserIdentity _userIdentity;
    private readonly ImageQueryService _imageQuery;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _concurrency = new(MAX_CONCURRENT, MAX_CONCURRENT);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _gate = new();
    private readonly HashSet<long> _claimedIds = new();
    private readonly Dictionary<long, CancellationTokenSource> _itemCts = new();
    private bool _disposed;

    /// <summary>Raised on any queue state change (new item, progress, status).</summary>
    public event Action? Changed;

    public DownloadQueueService(
        DatabaseService database,
        INasConnectorFactory nasFactory,
        AppSettingsService settings,
        IUserIdentity userIdentity,
        ImageQueryService imageQuery,
        Action<string>? log = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _nasFactory = nasFactory ?? throw new ArgumentNullException(nameof(nasFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _userIdentity = userIdentity ?? throw new ArgumentNullException(nameof(userIdentity));
        _imageQuery = imageQuery ?? throw new ArgumentNullException(nameof(imageQuery));
        _log = log ?? (_ => { });

        // Kick off the background worker. Tasks spawn off it as items arrive.
        _ = Task.Run(() => WorkerLoopAsync(_shutdownCts.Token));
    }

    // ─── Public API ────────────────────────────────────────────────────────

    /// <summary>Inserts a pending row and signals the worker.</summary>
    public Task EnqueueAsync(long assetId, string localTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localTarget);
        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO download_queue
                    (user_id, asset_id, local_target, status, progress, error,
                     created_at, updated_at)
                VALUES
                    ($uid, $aid, $target, 'pending', 0, NULL, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$uid", _userIdentity.CombinedId);
            cmd.Parameters.AddWithValue("$aid", assetId);
            cmd.Parameters.AddWithValue("$target", localTarget);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        });
        _signal.Release();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the queue item: signals the in-flight task (if any), deletes
    /// the <c>.part</c> sidecar and stamps status=cancelled.
    /// </summary>
    public Task CancelAsync(long id)
    {
        CancelItemCts(id);
        DownloadQueueItem? item = GetById(id);
        if (item is not null)
            TryDeleteFile(item.LocalTarget + ".part");
        UpdateStatus(id, "cancelled", error: "Cancelled by user");
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Resets status to pending and clears the error; signals worker.</summary>
    public Task RetryAsync(long id)
    {
        UpdateStatus(id, "pending", error: null, progress: 0);
        _signal.Release();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>All queue rows, newest first.</summary>
    public List<DownloadQueueItem> GetAll() => _db.Run(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, asset_id, local_target, status, progress, error,
                   created_at, updated_at
            FROM download_queue
            ORDER BY id DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var items = new List<DownloadQueueItem>();
        while (reader.Read())
        {
            long assetId = reader.GetInt64(1);
            ImageRecord? image = _imageQuery.GetById(assetId);
            items.Add(new DownloadQueueItem
            {
                Id = reader.GetInt64(0),
                AssetId = assetId,
                FileName = image?.FileName ?? "?",
                LocalTarget = reader.GetString(2),
                Status = reader.GetString(3),
                Progress = reader.GetDouble(4),
                Error = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = DbTime.Parse(reader.GetString(6)),
                UpdatedAt = DbTime.Parse(reader.GetString(7)),
                NasConnectionId = image?.NasConnectionId,
                Source = image?.Source ?? ImageSource.Local,
            });
        }
        return items;
    });

    /// <summary>Items still waiting to be picked up by a worker slot.</summary>
    public List<DownloadQueueItem> GetPending() =>
        GetAll().Where(i => i.Status == "pending").ToList();

    /// <summary>Single row by id; null if absent.</summary>
    public DownloadQueueItem? GetById(long id) => _db.Run(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, asset_id, local_target, status, progress, error,
                   created_at, updated_at
            FROM download_queue
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        long assetId = reader.GetInt64(1);
        ImageRecord? image = _imageQuery.GetById(assetId);
        return new DownloadQueueItem
        {
            Id = reader.GetInt64(0),
            AssetId = assetId,
            FileName = image?.FileName ?? "?",
            LocalTarget = reader.GetString(2),
            Status = reader.GetString(3),
            Progress = reader.GetDouble(4),
            Error = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = DbTime.Parse(reader.GetString(6)),
            UpdatedAt = DbTime.Parse(reader.GetString(7)),
            NasConnectionId = image?.NasConnectionId,
            Source = image?.Source ?? ImageSource.Local,
        };
    });

    // ─── Background worker ─────────────────────────────────────────────────

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long? claimedId = null;
            lock (_gate)
            {
                foreach (DownloadQueueItem item in GetAll())
                {
                    if (item.Status == "pending" && !_claimedIds.Contains(item.Id))
                    {
                        _claimedIds.Add(item.Id);
                        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _itemCts[item.Id] = linked;
                        claimedId = item.Id;
                        break;
                    }
                }
            }

            if (claimedId is long id)
            {
                CancellationTokenSource cts;
                lock (_gate) { cts = _itemCts[id]; }
                _ = Task.Run(() => ProcessItemAsync(id, cts.Token), ct);
                continue;
            }

            try
            {
                await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessItemAsync(long queueId, CancellationToken itemCt)
    {
        CancellationTokenSource? ctsToDispose = null;
        try
        {
            await _concurrency.WaitAsync(itemCt);
            try
            {
                if (itemCt.IsCancellationRequested)
                {
                    OnCancelled(queueId);
                    return;
                }

                DownloadQueueItem? item = GetById(queueId);
                if (item is null || item.Status is "cancelled" or "completed")
                    return;

                UpdateStatus(queueId, "downloading");
                Changed?.Invoke();

                ImageRecord? image = _imageQuery.GetById(item.AssetId);
                if (image is null)
                {
                    MarkFailed(queueId, "Image record not found.");
                    return;
                }

                EnsureDirectory(item.LocalTarget);

                if (image.Source == ImageSource.Local)
                {
                    await ProcessLocalAsync(queueId, image, item.LocalTarget, itemCt);
                }
                else
                {
                    await ProcessRemoteAsync(queueId, image, item.LocalTarget, itemCt);
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
            OnCancelled(queueId);
        }
        catch (Exception ex)
        {
            _log($"[DOWNLOAD] Unexpected error for queue {queueId}: {ex}");
            MarkFailed(queueId, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _claimedIds.Remove(queueId);
                if (_itemCts.TryGetValue(queueId, out ctsToDispose))
                    _itemCts.Remove(queueId);
            }
            // Wake the worker so it re-polls for any pending items (esp. after
            // a slot frees up).
            if (!_disposed)
                _signal.Release();
        }
        if (ctsToDispose is not null)
            ctsToDispose.Dispose();
    }

    private async Task ProcessLocalAsync(long queueId, ImageRecord image, string localTarget, CancellationToken itemCt)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            if (itemCt.IsCancellationRequested)
            {
                OnCancelled(queueId);
                return;
            }
            try
            {
                File.Copy(image.FilePath, localTarget, overwrite: true);
                UpdateStatus(queueId, "completed", progress: 1.0);
                Changed?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log($"[DOWNLOAD] Local copy attempt {attempt} for queue {queueId} failed: {ex.Message}");
                if (attempt < MAX_ATTEMPTS)
                    await Task.Delay(BACKOFF_MS[attempt - 1], itemCt);
            }
        }
        MarkFailed(queueId, lastError?.Message ?? "All copy attempts failed.");
    }

    private async Task ProcessRemoteAsync(long queueId, ImageRecord image, string localTarget, CancellationToken itemCt)
    {
        if (image.NasConnectionId is not int connId)
        {
            MarkFailed(queueId, "Remote image has no NAS connection id.");
            return;
        }
        NasConnectionSpec? spec = _settings.NasConnections.FirstOrDefault(c => c.Id == connId);
        if (spec is null)
        {
            MarkFailed(queueId, $"NAS connection {connId} is not configured.");
            return;
        }
        string? password = _settings.GetNasPassword(connId);

        string partPath = localTarget + ".part";
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            if (itemCt.IsCancellationRequested)
            {
                OnCancelled(queueId);
                return;
            }

            // Re-check user cancellation between attempts.
            DownloadQueueItem? current = GetById(queueId);
            if (current is null || current.Status == "cancelled")
                return;

            INasConnector? connector = null;
            try
            {
                connector = _nasFactory.Create(spec, password);
                long fileSize = image.FileSize;
                long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

                // Already-complete .part: just rename.
                if (fileSize > 0 && offset >= fileSize)
                {
                    File.Move(partPath, localTarget, overwrite: true);
                    UpdateStatus(queueId, "completed", progress: 1.0);
                    Changed?.Invoke();
                    return;
                }

                bool freshNeeded = false;
                if (offset > 0)
                {
                    try
                    {
                        await DownloadAppendAsync(
                            connector, image.FilePath, partPath, offset, fileSize,
                            queueId, itemCt);
                    }
                    catch (NasConnectionException ex)
                        when (ex.Kind == NasConnectionKind.RangeNotSatisfiable)
                    {
                        // Server can't resume; discard .part and restart from 0.
                        TryDeleteFile(partPath);
                        offset = 0;
                        freshNeeded = true;
                    }
                }
                else
                {
                    freshNeeded = true;
                }

                if (freshNeeded)
                {
                    await DownloadAppendAsync(
                        connector, image.FilePath, partPath, 0, fileSize,
                        queueId, itemCt);
                }

                // .part should now contain the whole file. Move to final.
                File.Move(partPath, localTarget, overwrite: true);
                UpdateStatus(queueId, "completed", progress: 1.0);
                Changed?.Invoke();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log($"[DOWNLOAD] Remote attempt {attempt} for queue {queueId} failed: {ex.Message}");
                if (attempt < MAX_ATTEMPTS)
                    await Task.Delay(BACKOFF_MS[attempt - 1], itemCt);
            }
            finally
            {
                if (connector is IDisposable d)
                    d.Dispose();
            }
        }

        TryDeleteFile(partPath);
        MarkFailed(queueId, lastError?.Message ?? "All download attempts failed.");
    }

    /// <summary>
    /// Loops <see cref="INasConnector.DownloadRangeAsync"/> in CHUNK_SIZE
    /// chunks from <paramref name="startOffset"/> to <paramref name="fileSize"/>,
    /// appending each chunk to <paramref name="partPath"/>. Updates DB
    /// progress after every chunk.
    /// </summary>
    private async Task DownloadAppendAsync(
        INasConnector connector, string remotePath, string partPath,
        long startOffset, long fileSize, long queueId, CancellationToken ct)
    {
        const FileOptions opts = FileOptions.Asynchronous | FileOptions.SequentialScan;
        long offset = startOffset;
        try
        {
            using var fs = new FileStream(partPath, FileMode.Append, FileAccess.Write,
                FileShare.None, bufferSize: 81920, options: opts);
            while (fileSize == 0 || offset < fileSize)
            {
                ct.ThrowIfCancellationRequested();
                long remaining = fileSize - offset;
                int chunk = (int)Math.Min(CHUNK_SIZE, remaining > 0 ? remaining : CHUNK_SIZE);
                byte[] bytes = await connector.DownloadRangeAsync(remotePath, offset, chunk, ct);
                if (bytes.Length == 0)
                    break;
                await fs.WriteAsync(bytes, 0, bytes.Length, ct);
                offset += bytes.Length;
                if (fileSize > 0)
                    UpdateProgress(queueId, (double)offset / fileSize);
                // Server returned fewer bytes than requested → EOF.
                if (bytes.Length < chunk)
                    break;
            }
            await fs.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // M5-Z (Release-only test failure): the worker holds an exclusive
            // FileShare.None lock on .part during the loop; user-triggered
            // CancelAsync.TryDeleteFile silently fails because the file is
            // locked. By the time we get here the using-block has disposed
            // the FileStream (releasing the lock), so we can delete the .part
            // ourselves before re-throwing — otherwise the test sees a stale
            // .part file race-conditionally.
            TryDeleteFile(partPath);
            throw;
        }
    }

    // ─── DB mutations ───────────────────────────────────────────────────────

    private void UpdateStatus(long id, string status, string? error = null, double? progress = null)
    {
        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE download_queue
                SET status = $status,
                    error = $error,
                    progress = COALESCE($progress, progress),
                    updated_at = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddNullable("$error", error);
            cmd.Parameters.AddNullable("$progress", progress);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    private void UpdateProgress(long id, double progress)
    {
        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE download_queue
                SET progress = $progress, updated_at = $now
                WHERE id = $id AND status = 'downloading';
                """;
            cmd.Parameters.AddWithValue("$progress", progress);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
        Changed?.Invoke();
    }

    private void MarkFailed(long id, string error)
    {
        UpdateStatus(id, "failed", error: error);
        Changed?.Invoke();
    }

    private void OnCancelled(long id)
    {
        DownloadQueueItem? item = GetById(id);
        if (item is not null)
            TryDeleteFile(item.LocalTarget + ".part");
        UpdateStatus(id, "cancelled", error: "Cancelled");
        Changed?.Invoke();
    }

    private void CancelItemCts(long id)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_itemCts.TryGetValue(id, out cts))
                return;
        }
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void EnsureDirectory(string localTarget)
    {
        string? dir = Path.GetDirectoryName(localTarget);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* Windows file locks — best effort. */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Shuts down the worker and cancels any in-flight downloads. Disposing
    /// is required at app shutdown (App.OnExit).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Cancel();
        // Cancel every per-item CTS so blocked workers wake up promptly.
        lock (_gate)
        {
            foreach (var cts in _itemCts.Values)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
        // Drain the signal so the worker exits its wait.
        try { _signal.Release(); } catch (SemaphoreFullException) { }
        _shutdownCts.Dispose();
        _concurrency.Dispose();
        _signal.Dispose();
    }
}
