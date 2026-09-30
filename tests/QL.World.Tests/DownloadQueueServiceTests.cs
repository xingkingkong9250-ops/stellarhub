using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M5-G: <see cref="DownloadQueueService"/> contract — enqueue, local copy,
/// remote range-resume, RangeNotSatisfiable fallback, retry, cancellation,
/// and the 2-slot concurrency cap.
/// </summary>
public sealed class DownloadQueueServiceTests : IDisposable
{
    private readonly string _directory;

    public DownloadQueueServiceTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best-effort temp cleanup. */ }
    }

    [Fact]
    public async Task Enqueue_inserts_pending_row()
    {
        using var harness = await Harness.NewAsync(_directory);
        long imageId = harness.InsertLocalImage("a.png");

        await harness.Service.EnqueueAsync(imageId, Path.Combine(_directory, "out.png"));

        var items = harness.Service.GetAll();
        Assert.Single(items);
        Assert.Equal("pending", items[0].Status);
        Assert.Equal(imageId, items[0].AssetId);
        Assert.Equal(0, items[0].Progress);
    }

    [Fact]
    public async Task Local_source_completes_via_FileCopy()
    {
        using var harness = await Harness.NewAsync(_directory);
        string src = TestImageFactory.CreatePng(_directory, "src.png", width: 12, height: 8);
        long imageId = harness.InsertLocalImage("src.png", filePath: src);
        string target = Path.Combine(_directory, "copied.png");

        await harness.Service.EnqueueAsync(imageId, target);
        await harness.WaitForStatusAsync(imageId, "completed", TimeSpan.FromSeconds(3));

        Assert.True(File.Exists(target));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Remote_source_resumes_from_part_file()
    {
        // 10-byte "remote file"; .part already has the first 4 bytes.
        byte[] remoteBytes = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        using var harness = await Harness.NewAsync(_directory, remoteBytes);
        long imageId = harness.InsertRemoteImage("r.png", fileSize: remoteBytes.Length);
        string target = Path.Combine(_directory, "r.png");
        string part = target + ".part";
        File.WriteAllBytes(part, remoteBytes.Take(4).ToArray());

        await harness.Service.EnqueueAsync(imageId, target);
        await harness.WaitForStatusAsync(imageId, "completed", TimeSpan.FromSeconds(3));

        Assert.True(File.Exists(target));
        Assert.Equal(remoteBytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(part), "part file should have been renamed to final");
    }

    [Fact]
    public async Task Remote_source_falls_back_to_fresh_on_RangeNotSatisfiable()
    {
        // Stub throws RangeNotSatisfiable for any offset > 0, forcing fresh
        // restart from offset 0. Service should delete .part and re-download.
        byte[] remoteBytes = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        using var harness = await Harness.NewAsync(_directory, remoteBytes, rejectResumeOffset: true);
        long imageId = harness.InsertRemoteImage("r.png", fileSize: remoteBytes.Length);
        string target = Path.Combine(_directory, "r.png");
        string part = target + ".part";
        // Seed a stale .part so resume is attempted (and rejected).
        File.WriteAllBytes(part, new byte[] { 99, 99, 99, 99 });

        await harness.Service.EnqueueAsync(imageId, target);
        await harness.WaitForStatusAsync(imageId, "completed", TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(target));
        Assert.Equal(remoteBytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(part), "stale .part must be deleted and final file renamed");
    }

    [Fact]
    public async Task Three_failures_marks_item_failed()
    {
        // Stub throws ProtocolError on every range request → service retries 3
        // times with 1s/2s/4s backoff → finally marks failed.
        using var harness =
            await Harness.NewAsync(_directory, fileBytes: null, alwaysFail: true);
        long imageId = harness.InsertRemoteImage("bad.png", fileSize: 10);
        string target = Path.Combine(_directory, "bad.png");

        await harness.Service.EnqueueAsync(imageId, target);
        // 3 attempts × (1 immediate fail + backoff): ~1s + 2s = 3s, allow 15s.
        var item = await harness.WaitForStatusAsync(imageId,
            new[] { "failed" }, TimeSpan.FromSeconds(15));

        Assert.Equal("failed", item.Status);
        Assert.False(string.IsNullOrEmpty(item.Error));
        Assert.False(File.Exists(target + ".part"), "leftover .part should be cleaned");
    }

    [Fact]
    public async Task Cancel_marks_cancelled_and_deletes_part()
    {
        // Stub delays each chunk by 200 ms; test cancels mid-flight.
        byte[] remoteBytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var harness = await Harness.NewAsync(_directory, remoteBytes, chunkDelayMs: 200);
        long imageId = harness.InsertRemoteImage("big.png", fileSize: remoteBytes.Length);
        string target = Path.Combine(_directory, "big.png");
        string part = target + ".part";

        await harness.Service.EnqueueAsync(imageId, target);
        // Wait for the worker to actually start (status → downloading).
        await harness.WaitForStatusAsync(imageId, "downloading", TimeSpan.FromSeconds(2));

        await harness.Service.CancelAsync(imageId);
        var item = await harness.WaitForStatusAsync(imageId,
            new[] { "cancelled" }, TimeSpan.FromSeconds(3));

        Assert.Equal("cancelled", item.Status);
        // M5-Z race fix: CancelAsync updates status synchronously and invokes
        // Changed, but the worker may still be inside DownloadAppendAsync's
        // await Task.Delay (stub) or its using-FFileStream scope. The worker's
        // catch (OperationCanceledException) deletes .part AFTER its FileStream
        // is disposed (releasing the FileShare.None lock). Poll up to 3 s for
        // the worker to release — without this, Release builds race-fail
        // because the synchronous assertion runs before the worker exits its
        // await. OnCancelled also runs on the worker's catch path as defense.
        Assert.True(WaitForCondition(() => !File.Exists(part), TimeSpan.FromSeconds(3)),
            ".part must be deleted on cancel (within 3s)");
    }

    private static bool WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public async Task Concurrency_capped_at_two()
    {
        // 5 items; each chunk takes 30 ms; service should run at most 2 in parallel.
        byte[] remoteBytes = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        using var harness = await Harness.NewAsync(_directory, remoteBytes, chunkDelayMs: 30);
        harness.Stub.ResetPeak();

        var targets = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            long imageId = harness.InsertRemoteImage($"img{i}.png", fileSize: remoteBytes.Length);
            string target = Path.Combine(_directory, $"img{i}.png");
            targets.Add(target);
            await harness.Service.EnqueueAsync(imageId, target);
        }

        // Wait for all 5 to finish.
        foreach (string t in targets)
        {
            string name = Path.GetFileName(t);
            // Look up the queue row by local_target — we know each target is unique.
            await AssertCompletesAsync(harness, t, TimeSpan.FromSeconds(5));
        }

        Assert.True(harness.Stub.PeakConcurrent <= 2,
            $"peak concurrent was {harness.Stub.PeakConcurrent}, expected ≤ 2");
        Assert.True(harness.Stub.PeakConcurrent >= 2,
            $"peak concurrent was {harness.Stub.PeakConcurrent}, expected saturation at the cap");
        foreach (string t in targets)
            Assert.True(File.Exists(t), $"final file missing: {t}");
    }

    private static async Task AssertCompletesAsync(Harness harness, string localTarget, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var item = harness.Service.GetAll().FirstOrDefault(i => i.LocalTarget == localTarget);
            if (item is { Status: "completed" or "failed" or "cancelled" })
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"Queue item for {localTarget} did not reach a terminal state within {timeout}.");
    }

    // ─── Harness + stubs ────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public TestLibrary Library { get; }
        public AppSettingsService Settings { get; }
        public TestNasFactory Factory { get; }
        public TestNasConnector Stub { get; }
        public DownloadQueueService Service { get; }

        private Harness(TestLibrary library, AppSettingsService settings,
            TestNasFactory factory, TestNasConnector stub,
            DownloadQueueService service)
        {
            Library = library;
            Settings = settings;
            Factory = factory;
            Stub = stub;
            Service = service;
        }

        public static async Task<Harness> NewAsync(
            string root, byte[]? fileBytes = null,
            bool alwaysFail = false, bool rejectResumeOffset = false,
            int chunkDelayMs = 0)
        {
            var library = new TestLibrary();
            var settings = new AppSettingsService(library.DirectoryRoot);
            settings.Load();
            settings.AddNasConnection(new NasConnectionSpec
            {
                Name = "test-nas", Host = "example.com", RootPath = "/", UseTls = true,
            });

            var stub = new TestNasConnector(fileBytes ?? Array.Empty<byte>(),
                alwaysFail, rejectResumeOffset, chunkDelayMs);
            var factory = new TestNasFactory(stub);

            var userIdentity = new UserIdentityService();
            var imageQuery = new ImageQueryService(library.Db);
            var service = new DownloadQueueService(
                library.Db, factory, settings, userIdentity, imageQuery, log: null);
            // Give the worker a tick to spin up so the first Enqueue races
            // cleanly with the poll loop.
            await Task.Yield();
            return new Harness(library, settings, factory, stub, service);
        }

        public long InsertLocalImage(string name, string? filePath = null)
        {
            long id = Library.InsertImage(filePath: filePath, hash: name);
            // Default InsertImage leaves source='local', nas_connection_id=NULL.
            return id;
        }

        public long InsertRemoteImage(string name, long fileSize)
        {
            long id = Library.InsertImage(hash: name);
            // Promote to remote + tie to NAS connection id = 1 (the one
            // added in NewAsync).
            Library.Db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE images
                    SET source = 'remote',
                        nas_connection_id = 1,
                        file_size = $size
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$size", fileSize);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            });
            return id;
        }

        public async Task<DownloadQueueItem> WaitForStatusAsync(
            long assetId, string status, TimeSpan timeout)
            => await WaitForStatusAsync(assetId, new[] { status }, timeout);

        public async Task<DownloadQueueItem> WaitForStatusAsync(
            long assetId, string[] statuses, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var item = Service.GetAll().FirstOrDefault(i => i.AssetId == assetId);
                if (item is not null && statuses.Contains(item.Status))
                    return item;
                await Task.Delay(20);
            }
            // Diagnostic dump on timeout.
            var current = Service.GetAll().FirstOrDefault(i => i.AssetId == assetId);
            throw new TimeoutException(
                $"Item for asset {assetId} did not reach [{string.Join("/", statuses)}] " +
                $"within {timeout}. Last status: {current?.Status ?? "<missing>"} " +
                $"error: {current?.Error ?? "<none>"}");
        }

        public void Dispose()
        {
            try { Service.Dispose(); }
            finally { Library.Dispose(); }
        }
    }

    /// <summary>
    /// Stub factory: returns the same connector instance for every Create call
    /// so the test can observe aggregate behavior (peak concurrency etc).
    /// </summary>
    private sealed class TestNasFactory : INasConnectorFactory
    {
        private readonly TestNasConnector _connector;
        public TestNasFactory(TestNasConnector connector) => _connector = connector;

        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword) => _connector;
    }

    /// <summary>
    /// Configurable stub: honors Range requests by default, can be told to
    /// always fail (ProtocolError) or reject any offset > 0
    /// (RangeNotSatisfiable) to exercise the fresh-restart path.
    /// </summary>
    private sealed class TestNasConnector : INasConnector, IDisposable
    {
        private readonly byte[] _bytes;
        private readonly bool _alwaysFail;
        private readonly bool _rejectResumeOffset;
        private readonly int _chunkDelayMs;
        private int _currentConcurrent;
        private int _peakConcurrent;

        public TestNasConnector(byte[] bytes, bool alwaysFail,
            bool rejectResumeOffset, int chunkDelayMs)
        {
            _bytes = bytes;
            _alwaysFail = alwaysFail;
            _rejectResumeOffset = rejectResumeOffset;
            _chunkDelayMs = chunkDelayMs;
            Spec = new NasConnectionSpec { Id = 1, Name = "test-nas" };
        }

        public NasConnectionSpec Spec { get; }
        public int PeakConcurrent => Volatile.Read(ref _peakConcurrent);
        public void ResetPeak() => Interlocked.Exchange(ref _peakConcurrent, 0);

        public async Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken ct)
        {
            TrackPeakIncrement();
            try
            {
                if (_chunkDelayMs > 0)
                    await Task.Delay(_chunkDelayMs, ct);

                if (_alwaysFail)
                    throw new NasConnectionException(
                        NasConnectionKind.ProtocolError, "stub: always fails");

                if (_rejectResumeOffset && offset > 0)
                    throw new NasConnectionException(
                        NasConnectionKind.RangeNotSatisfiable,
                        $"stub: cannot resume at offset={offset}");

                if (offset >= _bytes.Length)
                    return Array.Empty<byte>();
                int take = (int)Math.Min(length, _bytes.Length - offset);
                byte[] result = new byte[take];
                Array.Copy(_bytes, (int)offset, result, 0, take);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }

        private void TrackPeakIncrement()
        {
            int c = Interlocked.Increment(ref _currentConcurrent);
            int spin = 0;
            while (true)
            {
                int old = Volatile.Read(ref _peakConcurrent);
                if (c <= old) break;
                if (Interlocked.CompareExchange(ref _peakConcurrent, c, old) == old) break;
                if (++spin > 64) break;
            }
        }

        // Unused methods — service only invokes DownloadRangeAsync for remote rows.
        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DownloadFileAsync(string p, string d, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UploadFileAsync(string l, string r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task MakeDirectoryAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose() { /* no-op */ }
    }
}
