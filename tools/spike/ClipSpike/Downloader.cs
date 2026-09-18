using System.Net.Http.Headers;

namespace ClipSpike;

/// <summary>
/// Downloads files from HuggingFace with hf-mirror.com fallback and
/// resume support (.part + Range). Reports SHA-256 and size.
/// </summary>
public static class Downloader
{
    private const string PrimaryBase = "https://huggingface.co";
    private const string MirrorBase = "https://hf-mirror.com";

    /// <summary>Downloads {repo}/resolve/main/{path} to dest with resume.</summary>
    public static async Task<string> DownloadAsync(string repo, string path, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string partFile = dest + ".part";

        foreach (string baseUrl in new[] { PrimaryBase, MirrorBase })
        {
            try
            {
                bool ok = await TryDownloadAsync($"{baseUrl}/{repo}/resolve/main/{path}", dest, partFile, ct);
                if (ok)
                {
                    Console.WriteLine($"[download] OK via {baseUrl}");
                    return ComputeSha(dest);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException
                or UnauthorizedAccessException or NotSupportedException)
            {
                Console.WriteLine($"[download] {baseUrl} failed: {ex.Message}");
                if (ct.IsCancellationRequested) throw;
            }
        }
        throw new IOException($"All sources failed for {path}");
    }

    private static async Task<bool> TryDownloadAsync(string url, string dest, string partFile, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        long existing = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 416 = range beyond EOF (file complete?) or server rejects ranges; restart.
        if ((int)response.StatusCode == 416)
        {
            File.Delete(partFile);
            existing = 0;
            request.Headers.Range = null;
            response.Dispose();
            var fresh = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            fresh.EnsureSuccessStatusCode();
            await StreamToFileAsync(fresh, partFile, 0, ct);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            // Mirror may not honor Range: 200 means full body, restart from 0.
            long resumeFrom = (int)response.StatusCode == 206 ? existing : 0;
            await StreamToFileAsync(response, partFile, resumeFrom, ct);
        }

        File.Move(partFile, dest, overwrite: true);
        return true;
    }

    private static async Task StreamToFileAsync(HttpResponseMessage response, string partFile, long startAt, CancellationToken ct)
    {
        await using var network = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(partFile, startAt == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        if (startAt > 0) file.Seek(startAt, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        long total = startAt + (response.Content.Headers.ContentLength ?? -1);
        int read;
        while ((read = await network.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            if (total > 0)
            {
                long done = file.Length;
                Console.Write($"\r    {done / (1024 * 1024)}MB / {total / (1024 * 1024)}MB ({done * 100 / total}%)   ");
            }
        }
        Console.WriteLine();
    }

    public static string ComputeSha(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
