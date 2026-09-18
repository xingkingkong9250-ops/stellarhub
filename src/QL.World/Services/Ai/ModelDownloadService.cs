using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using QL.World.Models;

namespace QL.World.Services.Ai;

/// <summary>
/// Downloads the local CLIP vision model into {dataRoot}\models\ with
/// three channels per the PRD: HTTP download (primary → hf-mirror fallback,
/// .part + Range resume), manual import of a user-provided file, and delete.
/// Everything is verified against ModelManifest SHA-256; corrupted files are
/// never left in place under the final name.
/// </summary>
public sealed class ModelDownloadService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(30);

    private readonly string _modelsDirectory;
    private readonly string _expectedSha256;
    private readonly long _expectedSizeBytes;
    private readonly Action<string> _log;
    private readonly HttpClient _http;

    public ModelDownloadService(
        string dataRoot,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
        : this(dataRoot, ModelManifest.ModelSha256, ModelManifest.ModelSizeBytes, log, httpHandler)
    {
    }

    /// <summary>Test constructor: overrides the expected digest and size.</summary>
    internal ModelDownloadService(
        string dataRoot,
        string expectedSha256,
        long expectedSizeBytes,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        _modelsDirectory = Path.Combine(dataRoot, "models");
        _expectedSha256 = expectedSha256;
        _expectedSizeBytes = expectedSizeBytes;
        _log = log ?? (_ => { });
        _http = httpHandler is null
            ? new HttpClient { Timeout = HttpTimeout }
            : new HttpClient(httpHandler) { Timeout = HttpTimeout };
    }

    public string ModelFilePath => Path.Combine(_modelsDirectory, ModelManifest.ModelFileName);
    private string PartFilePath => ModelFilePath + ".part";

    /// <summary>
    /// Stat + full SHA-256 streaming check of the local model file.
    /// </summary>
    public LocalModelState CheckLocalState()
    {
        if (!File.Exists(ModelFilePath))
            return LocalModelState.NotDownloaded;

        var info = new FileInfo(ModelFilePath);
        if (info.Length != _expectedSizeBytes)
            return LocalModelState.Corrupted;

        return string.Equals(ComputeSha(ModelFilePath), _expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? LocalModelState.Ready
            : LocalModelState.Corrupted;
    }

    /// <summary>
    /// Downloads the model from the primary URL with mirror fallback.
    /// A cancelled or failed download keeps the .part file so the next call
    /// resumes. Throws when every source fails or the final SHA does not match.
    /// </summary>
    public async Task DownloadAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectory);

        bool viaMirror = false;
        try
        {
            await TryDownloadAsync(ModelManifest.SourceUrl, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException
            or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log($"[MODEL] Primary source failed: {ex.Message}");
            viaMirror = true;
        }

        if (viaMirror)
        {
            // Mirror restarts from scratch (it may not honor Range).
            File.Delete(PartFilePath);
            await TryDownloadAsync(ModelManifest.MirrorUrl, progress, cancellationToken);
        }

        progress?.Report(new ModelDownloadProgress(
            ModelDownloadPhase.Verifying, 100, _expectedSizeBytes, _expectedSizeBytes, "校验 SHA-256…"));
        string sha = ComputeSha(ModelFilePath);
        if (!string.Equals(sha, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(ModelFilePath);
            throw new InvalidDataException(
                $"模型文件校验失败（SHA-256 不匹配）：{sha}，已删除，请重试或手动导入。");
        }

        progress?.Report(new ModelDownloadProgress(
            ModelDownloadPhase.Completed, 100, _expectedSizeBytes, _expectedSizeBytes, "完成"));
        _log("[MODEL] Download verified OK.");
    }

    /// <summary>
    /// Copies a user-provided model file into the models directory and
    /// verifies it. On SHA mismatch the copy is deleted and an exception thrown.
    /// </summary>
    public async Task<string> ImportManualAsync(string sourceFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("所选模型文件不存在。", sourceFile);

        Directory.CreateDirectory(_modelsDirectory);
        string tempPath = ModelFilePath + ".import";

        try
        {
            await using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await source.CopyToAsync(dest, cancellationToken);
            }

            string sha = ComputeSha(tempPath);
            if (!string.Equals(sha, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"所选文件校验失败（SHA-256 不匹配）：{sha}");

            File.Move(tempPath, ModelFilePath, overwrite: true);
            _log($"[MODEL] Manual import OK from {sourceFile}");
            return ModelFilePath;
        }
        finally
        {
            File.Delete(tempPath); // no-op when Move already renamed it
        }
    }

    /// <summary>Deletes the model file and any stray temp files. True when the model file existed.</summary>
    public bool DeleteModel()
    {
        bool existed = File.Exists(ModelFilePath);
        foreach (string path in new[] { ModelFilePath, PartFilePath, ModelFilePath + ".import" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        return existed;
    }

    private async Task TryDownloadAsync(
        string url,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        long existing = File.Exists(PartFilePath) ? new FileInfo(PartFilePath).Length : 0;

        HttpResponseMessage response = await SendRangeAsync(url, existing > 0 ? existing : null, ct);
        if ((int)response.StatusCode == 416)
        {
            // Range beyond EOF or ranges unsupported: restart from scratch.
            // A new request is required — HttpRequestMessage cannot be resent.
            response.Dispose();
            File.Delete(PartFilePath);
            existing = 0;
            response = await SendRangeAsync(url, null, ct);
        }

        try
        {
            response.EnsureSuccessStatusCode();

            // A 200 (not 206) means the server ignored the Range: overwrite.
            long resumeFrom = (int)response.StatusCode == 206 ? existing : 0;
            await StreamToFileAsync(response, PartFilePath, resumeFrom, progress, ct);
        }
        finally
        {
            response.Dispose();
        }

        File.Move(PartFilePath, ModelFilePath, overwrite: true);
    }

    private async Task<HttpResponseMessage> SendRangeAsync(string url, long? rangeFrom, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (rangeFrom is { } from)
        {
            request.Headers.Range = new RangeHeaderValue(from, null);
            _log($"[MODEL] Resuming {url} from {from:N0} bytes");
        }
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task StreamToFileAsync(
        HttpResponseMessage response,
        string partFile,
        long startAt,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        await using var network = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(partFile, startAt == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        long total = _expectedSizeBytes;
        long lastReported = -1;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await network.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            long done = file.Length;
            // Throttle UI reports to ~1 per MB.
            if (progress is not null && done - lastReported >= (1 << 20))
            {
                lastReported = done;
                progress.Report(new ModelDownloadProgress(
                    ModelDownloadPhase.Downloading,
                    total > 0 ? done * 100.0 / total : 0,
                    done, total, string.Empty));
            }
        }
    }

    private static string ComputeSha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
