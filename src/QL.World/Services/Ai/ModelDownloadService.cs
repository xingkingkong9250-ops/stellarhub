using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using QL.World.Models;

namespace QL.World.Services.Ai;

/// <summary>
/// Per-file model spec: file name, expected SHA-256, expected size in bytes,
/// and primary/mirror URLs. A bundle is an ordered list of these.
/// </summary>
internal sealed record ModelFileSpec(
    string FileName,
    string Sha256,
    long SizeBytes,
    string SourceUrl,
    string MirrorUrl);

/// <summary>
/// Downloads the local CLIP model bundle (vision + text per the M3-E2 plan)
/// into {dataRoot}\models\ with three channels per the PRD: HTTP download
/// (primary → hf-mirror fallback, .part + Range resume), manual import of a
/// user-provided file (vision only; text model can only come from a download),
/// and delete. Every file is verified against its ModelManifest SHA-256;
/// corrupted files are never left in place under the final name.
/// </summary>
public sealed class ModelDownloadService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(30);

    private readonly string _modelsDirectory;
    private readonly IReadOnlyList<ModelFileSpec> _specs;
    private readonly Action<string> _log;
    private readonly HttpClient _http;

    /// <summary>Production: bundle = [vision_model.onnx, text_model.onnx] (M3-E2).</summary>
    public ModelDownloadService(
        string dataRoot,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
        : this(dataRoot, BuildDefaultBundle(), log, httpHandler)
    {
    }

    /// <summary>Test constructor: overrides the expected digest and size for a single (vision) file.</summary>
    internal ModelDownloadService(
        string dataRoot,
        string expectedSha256,
        long expectedSizeBytes,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
        : this(dataRoot,
            [new ModelFileSpec(ModelManifest.ModelFileName, expectedSha256, expectedSizeBytes,
                ModelManifest.SourceUrl, ModelManifest.MirrorUrl)],
            log, httpHandler)
    {
    }

    /// <summary>Test constructor: injects an explicit bundle.</summary>
    internal ModelDownloadService(
        string dataRoot,
        IReadOnlyList<ModelFileSpec> specs,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0) throw new ArgumentException("bundle must contain at least one file spec.", nameof(specs));
        _modelsDirectory = Path.Combine(dataRoot, "models");
        _specs = specs;
        _log = log ?? (_ => { });
        _http = httpHandler is null
            ? new HttpClient { Timeout = HttpTimeout }
            : new HttpClient(httpHandler) { Timeout = HttpTimeout };
    }

    private static IReadOnlyList<ModelFileSpec> BuildDefaultBundle() =>
    [
        new(ModelManifest.ModelFileName, ModelManifest.ModelSha256, ModelManifest.ModelSizeBytes,
            ModelManifest.SourceUrl, ModelManifest.MirrorUrl),
        new(ModelManifest.TextModelFileName, ModelManifest.TextModelSha256, ModelManifest.TextModelSizeBytes,
            ModelManifest.TextModelSourceUrl, ModelManifest.TextModelMirrorUrl),
    ];

    /// <summary>Path of the primary (vision) model file — first spec in the bundle.</summary>
    public string ModelFilePath => Path.Combine(_modelsDirectory, _specs[0].FileName);

    private string PathFor(ModelFileSpec spec) => Path.Combine(_modelsDirectory, spec.FileName);
    private string PartPathFor(ModelFileSpec spec) => PathFor(spec) + ".part";

    /// <summary>
    /// Combined stat + full SHA-256 streaming check of every file in the bundle.
    /// Returns NotDownloaded when the first spec is missing (typical: never
    /// started), Corrupted when any file is missing/wrong-size/wrong-SHA,
    /// Ready only when every file passes.
    /// </summary>
    public LocalModelState CheckLocalState()
    {
        foreach (ModelFileSpec spec in _specs)
        {
            string path = PathFor(spec);
            if (!File.Exists(path))
                return LocalModelState.NotDownloaded;
            if (new FileInfo(path).Length != spec.SizeBytes)
                return LocalModelState.Corrupted;
            if (!string.Equals(ComputeSha(path), spec.Sha256, StringComparison.OrdinalIgnoreCase))
                return LocalModelState.Corrupted;
        }
        return LocalModelState.Ready;
    }

    /// <summary>
    /// Downloads every file in the bundle sequentially from primary URLs with
    /// mirror fallback per file. A cancelled or failed download keeps the
    /// current file's .part so the next call resumes; on any failure the
    /// previously completed files in this call are rolled back (deleted) so
    /// the bundle is never half-installed. Throws when every source fails or
    /// the final SHA does not match.
    /// </summary>
    public async Task DownloadAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectory);

        var completedSpecs = new List<ModelFileSpec>();
        try
        {
            for (int i = 0; i < _specs.Count; i++)
            {
                ModelFileSpec spec = _specs[i];
                double basePercent = i * 100.0 / _specs.Count;
                double weight = 100.0 / _specs.Count;
                await DownloadOneAsync(spec, basePercent, weight, progress, cancellationToken);
                completedSpecs.Add(spec);
            }

            progress?.Report(new ModelDownloadProgress(
                ModelDownloadPhase.Completed, 100, 0, 0, "完成"));
            _log("[MODEL] Bundle download verified OK.");
        }
        catch
        {
            // Roll back any files completed earlier in this bundle attempt
            // so CheckLocalState reports NotDownloaded, not a half-bundle.
            foreach (ModelFileSpec spec in completedSpecs)
            {
                string path = PathFor(spec);
                if (File.Exists(path)) File.Delete(path);
            }
            throw;
        }
    }

    /// <summary>
    /// Copies a user-provided file into the models directory as the vision
    /// model (the bundle's first spec) and verifies it. On SHA mismatch the
    /// copy is deleted and an exception thrown. Text model can only be
    /// obtained via the download flow.
    /// </summary>
    public async Task<string> ImportManualAsync(string sourceFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("所选模型文件不存在。", sourceFile);

        Directory.CreateDirectory(_modelsDirectory);
        ModelFileSpec target = _specs[0];
        string tempPath = PathFor(target) + ".import";

        try
        {
            await using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await source.CopyToAsync(dest, cancellationToken);
            }

            string sha = ComputeSha(tempPath);
            if (!string.Equals(sha, target.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"所选文件校验失败（SHA-256 不匹配）：{sha}");

            File.Move(tempPath, PathFor(target), overwrite: true);
            _log($"[MODEL] Manual import OK for {target.FileName} from {sourceFile}");
            return PathFor(target);
        }
        finally
        {
            File.Delete(tempPath); // no-op when Move already renamed it
        }
    }

    /// <summary>Deletes every file in the bundle and any stray temp files.
    /// True when any model file existed.</summary>
    public bool DeleteModel()
    {
        bool existed = false;
        foreach (ModelFileSpec spec in _specs)
        {
            string path = PathFor(spec);
            if (File.Exists(path)) { existed = true; File.Delete(path); }
            foreach (string suffix in new[] { ".part", ".import" })
            {
                string temp = path + suffix;
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        return existed;
    }

    private async Task DownloadOneAsync(
        ModelFileSpec spec,
        double basePercent,
        double weight,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        bool viaMirror = false;
        try
        {
            await TryDownloadAsync(spec, spec.SourceUrl, basePercent, weight, progress, ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException
            or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            _log($"[MODEL] Primary source failed for {spec.FileName}: {ex.Message}");
            viaMirror = true;
        }

        if (viaMirror)
        {
            File.Delete(PartPathFor(spec));
            await TryDownloadAsync(spec, spec.MirrorUrl, basePercent, weight, progress, ct);
        }

        progress?.Report(new ModelDownloadProgress(
            ModelDownloadPhase.Verifying,
            basePercent + weight,
            spec.SizeBytes, spec.SizeBytes,
            $"校验 {spec.FileName} SHA-256…"));
        string sha = ComputeSha(PathFor(spec));
        if (!string.Equals(sha, spec.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(PathFor(spec));
            throw new InvalidDataException(
                $"{spec.FileName} 校验失败（SHA-256 不匹配）：{sha}，已删除，请重试。");
        }
        _log($"[MODEL] {spec.FileName} verified OK.");
    }

    private async Task TryDownloadAsync(
        ModelFileSpec spec,
        string url,
        double basePercent,
        double weight,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        string partPath = PartPathFor(spec);
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        HttpResponseMessage response = await SendRangeAsync(url, existing > 0 ? existing : null, ct);
        if ((int)response.StatusCode == 416)
        {
            // Range beyond EOF or ranges unsupported: restart from scratch.
            // A new request is required — HttpRequestMessage cannot be resent.
            response.Dispose();
            File.Delete(partPath);
            existing = 0;
            response = await SendRangeAsync(url, null, ct);
        }

        try
        {
            response.EnsureSuccessStatusCode();
            // A 200 (not 206) means the server ignored the Range: overwrite.
            long resumeFrom = (int)response.StatusCode == 206 ? existing : 0;
            await StreamToFileAsync(response, partPath, resumeFrom, spec, basePercent, weight, progress, ct);
        }
        finally
        {
            response.Dispose();
        }

        File.Move(partPath, PathFor(spec), overwrite: true);
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
        ModelFileSpec spec,
        double basePercent,
        double weight,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        await using var network = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(partFile, startAt == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        long total = spec.SizeBytes;
        long lastReported = -1;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await network.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            long done = file.Length;
            // Throttle UI reports to ~1 per MB; map to the [base, base+weight] window.
            if (progress is not null && done - lastReported >= (1 << 20))
            {
                lastReported = done;
                double inner = total > 0 ? done * 1.0 / total : 0;
                progress.Report(new ModelDownloadProgress(
                    ModelDownloadPhase.Downloading,
                    basePercent + inner * weight,
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
