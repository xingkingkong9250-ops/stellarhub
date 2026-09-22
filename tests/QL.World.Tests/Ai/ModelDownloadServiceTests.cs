using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using QL.World.Models;
using QL.World.Services.Ai;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// ModelDownloadService flows against a stubbed HttpMessageHandler:
/// full download, 206 resume, 416 restart, mirror fallback, SHA mismatch,
/// manual import and delete.
/// </summary>
public sealed class ModelDownloadServiceTests : IDisposable
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("ql-world-local-model-payload");
    private static readonly string PayloadSha =
        Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    private readonly TestLibrary _library = new();
    private readonly StubHandler _handler = new();

    private ModelDownloadService CreateService() =>
        new(_library.DirectoryRoot, PayloadSha, Payload.Length, httpHandler: _handler);

    private string ModelPath =>
        Path.Combine(_library.DirectoryRoot, "models", ModelManifest.ModelFileName);

    [Fact]
    public async Task Download_serves_final_file_and_reports_progress()
    {
        _handler.Responder = _ => Ok(Payload);
        var service = CreateService();
        var collector = new CollectingProgress();

        await service.DownloadAsync(collector, CancellationToken.None);

        Assert.True(File.Exists(ModelPath));
        Assert.Equal(Payload.Length, new FileInfo(ModelPath).Length);
        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
        Assert.Contains(ModelDownloadPhase.Completed, collector.Phases);
    }

    [Fact]
    public async Task Download_resumes_partial_file_with_range_header()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        string part = ModelPath + ".part";
        await File.WriteAllBytesAsync(part, Payload[..5]);
        _handler.Responder = request =>
        {
            RangeHeaderValue? range = request.Headers.Range;
            long from = range?.Ranges.FirstOrDefault()?.From ?? 0;
            byte[] slice = Payload[(int)from..];
            return Partial(slice, from, Payload.Length);
        };
        var service = CreateService();

        await service.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(Payload.Length, new FileInfo(ModelPath).Length);
        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Download_restarts_after_416()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        await File.WriteAllBytesAsync(ModelPath + ".part", [1, 2, 3, 4]);
        bool sawRange = false;
        _handler.Responder = request =>
        {
            if (request.Headers.Range is not null)
            {
                sawRange = true;
                return new HttpResponseMessage((HttpStatusCode)416);
            }
            return Ok(Payload);
        };
        var service = CreateService();

        await service.DownloadAsync(null, CancellationToken.None);

        Assert.True(sawRange);
        Assert.False(File.Exists(ModelPath + ".part"));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(ModelPath));
    }

    [Fact]
    public async Task Download_falls_back_to_mirror_when_primary_fails()
    {
        _handler.Responder = request =>
        {
            if (request.RequestUri!.Host == "huggingface.co")
                throw new HttpRequestException("primary down");
            return Ok(Payload);
        };
        var service = CreateService();

        await service.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
    }

    [Fact]
    public async Task Download_throws_and_cleans_up_on_sha_mismatch()
    {
        _handler.Responder = _ => Ok(Encoding.UTF8.GetBytes("tampered"));
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadAsync(null, CancellationToken.None));

        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(ModelPath + ".part"));
        Assert.Equal(LocalModelState.NotDownloaded, service.CheckLocalState());
    }

    [Fact]
    public async Task Download_throws_when_all_sources_fail()
    {
        _handler.Responder = _ => throw new HttpRequestException("offline");
        var service = CreateService();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.DownloadAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Import_manual_copies_verified_file()
    {
        string source = Path.Combine(_library.DirectoryRoot, "downloaded.onnx");
        await File.WriteAllBytesAsync(source, Payload);
        var service = CreateService();

        await service.ImportManualAsync(source, CancellationToken.None);

        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
    }

    [Fact]
    public async Task Import_manual_rejects_wrong_content_and_keeps_no_model()
    {
        string source = Path.Combine(_library.DirectoryRoot, "bad.onnx");
        await File.WriteAllBytesAsync(source, [9, 9, 9]);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportManualAsync(source, CancellationToken.None));

        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(ModelPath + ".import"));
    }

    [Fact]
    public void Check_local_state_reflects_file_conditions()
    {
        var service = CreateService();
        Assert.Equal(LocalModelState.NotDownloaded, service.CheckLocalState());

        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, Payload[..3]); // wrong size
        Assert.Equal(LocalModelState.Corrupted, service.CheckLocalState());

        File.WriteAllBytes(ModelPath, Payload);
        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
    }

    [Fact]
    public void Delete_model_removes_file_and_reports_existence()
    {
        var service = CreateService();
        Assert.False(service.DeleteModel());

        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, Payload);
        Assert.True(service.DeleteModel());
        Assert.False(File.Exists(ModelPath));
    }

    // ── M3-E2 bundle (vision + text) flows ───────────────────────────────

    private static readonly byte[] TextPayload = Encoding.UTF8.GetBytes("ql-world-text-model-payload-254mb-placeholder");
    private static readonly string TextPayloadSha =
        Convert.ToHexString(SHA256.HashData(TextPayload)).ToLowerInvariant();

    private const string VisionUrl = "https://huggingface.co/x/y/resolve/main/onnx/vision_model.onnx";
    private const string TextUrl = "https://huggingface.co/x/y/resolve/main/onnx/text_model.onnx";

    private string TextModelPath =>
        Path.Combine(_library.DirectoryRoot, "models", ModelManifest.TextModelFileName);

    private ModelDownloadService CreateBundleService() =>
        new(_library.DirectoryRoot,
        [
            new ModelFileSpec(ModelManifest.ModelFileName, PayloadSha, Payload.Length, VisionUrl, VisionUrl),
            new ModelFileSpec(ModelManifest.TextModelFileName, TextPayloadSha, TextPayload.Length, TextUrl, TextUrl),
        ], httpHandler: _handler);

    [Fact]
    public async Task Bundle_download_writes_and_verifies_both_files()
    {
        _handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("text_model") ? Ok(TextPayload) : Ok(Payload);
        var service = CreateBundleService();

        await service.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(ModelPath));
        Assert.Equal(TextPayload, await File.ReadAllBytesAsync(TextModelPath));
        Assert.Equal(LocalModelState.Ready, service.CheckLocalState());
    }

    [Fact]
    public async Task Bundle_rolls_back_first_file_when_second_download_fails()
    {
        _handler.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("text_model"))
                throw new HttpRequestException("text source down");
            return Ok(Payload);
        };
        var service = CreateBundleService();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.DownloadAsync(null, CancellationToken.None));

        // Vision was completed before text failed → rollback deleted it,
        // leaving no half-installed bundle.
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(TextModelPath));
        Assert.Equal(LocalModelState.NotDownloaded, service.CheckLocalState());
    }

    [Fact]
    public void Bundle_check_state_reports_missing_text_as_not_downloaded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, Payload);
        var service = CreateBundleService();

        Assert.Equal(LocalModelState.NotDownloaded, service.CheckLocalState());
    }

    [Fact]
    public void Bundle_check_state_reports_wrong_size_text_as_corrupted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, Payload);
        File.WriteAllBytes(TextModelPath, TextPayload[..3]);
        var service = CreateBundleService();

        Assert.Equal(LocalModelState.Corrupted, service.CheckLocalState());
    }

    [Fact]
    public void Bundle_delete_removes_both_files()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, Payload);
        File.WriteAllBytes(TextModelPath, TextPayload);
        var service = CreateBundleService();

        Assert.True(service.DeleteModel());
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(TextModelPath));
    }

    [Fact]
    public async Task Bundle_progress_advances_through_second_half_during_text_download()
    {
        _handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("text_model") ? Ok(TextPayload) : Ok(Payload);
        var service = CreateBundleService();
        var collector = new CollectingProgress();

        await service.DownloadAsync(collector, CancellationToken.None);

        // Per-file Verifying reports at phase boundaries: vision → 50, text → 100.
        // (Downloading ticks are throttled to ~1/MB and may not fire for tiny test payloads.)
        Assert.Contains(50.0, collector.Percents);
        Assert.Contains(100.0, collector.Percents);
        Assert.Contains(ModelDownloadPhase.Completed, collector.Phases);
    }

    private static HttpResponseMessage Ok(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private static HttpResponseMessage Partial(byte[] slice, long from, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Content.Headers.ContentRange =
            new ContentRangeHeaderValue(from, from + slice.Length - 1, total);
        return response;
    }

    /// <summary>Synchronous progress recorder (Progress&lt;T&gt; is async and racy in tests).</summary>
    private sealed class CollectingProgress : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadPhase> Phases { get; } = [];
        public List<double> Percents { get; } = [];
        public double? LastDownloadPercent { get; private set; }
        public void Report(ModelDownloadProgress value)
        {
            Phases.Add(value.Phase);
            Percents.Add(value.Percent);
            if (value.Phase == ModelDownloadPhase.Downloading)
                LastDownloadPercent = value.Percent;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => throw new InvalidOperationException("Responder not configured");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Responder(request));
    }

    public void Dispose()
    {
        _library.Dispose();
    }
}
