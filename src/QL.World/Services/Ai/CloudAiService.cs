using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Models.Taxonomy;

namespace QL.World.Services.Ai;

/// <summary>
/// OpenAI-compatible cloud AI backend. Calls chat/completions for
/// categorization + tagging, and (optionally) embeddings for vectors.
/// Only HTTPS base URLs are accepted; failures return error results, not exceptions.
/// </summary>
public sealed class CloudAiService : IAiService, IVlmImageCompare
{
    private const int TIMEOUT_SECONDS = 30;

    private readonly CloudAiConfig _config;
    private readonly HttpClient _http;

    public CloudAiService(CloudAiConfig config, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;

        var clientHandler = handler ?? new HttpClientHandler();
        _http = new HttpClient(clientHandler, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(TIMEOUT_SECONDS),
        };
    }

    public string Mode => "Cloud";
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_config.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.ModelName) &&
        !string.IsNullOrWhiteSpace(_config.EncryptedApiKey);
    public bool SupportsEmbeddings => !string.IsNullOrWhiteSpace(_config.EmbeddingModelName);

    /// <summary>
    /// M12-D: System prompt built from <see cref="TagTaxonomy"/>. Lists all
    /// category and filter tag groups (文件格式/色彩倾向 handled locally). No
    /// category output — derived from tags via TaxonomyService downstream.
    /// </summary>
    private static readonly string SystemPrompt = BuildSystemPrompt();

    /// <summary>
    /// 0.16.0 DUP-E: 双图查重终审提示词。判断「同一画面的重复副本」，
    /// 允许转码/缩放/调色/轻微裁剪，输出 same + confidence。
    /// </summary>
    private const string VlmComparePrompt =
        "你是图片查重助手。判断用户给出的两张图片是否为同一张画面的重复副本" +
        "（允许转码、缩放、调色、轻微裁剪）。" +
        "输出 JSON：{\"same\":true|false,\"confidence\":0.0-1.0}。只输出 JSON，无多余文字。";

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.Append("你是 3D 美术图片标签助手。分析图片，从下方词表中识别匹配的标签，");
        sb.Append("输出 JSON：{\"tags\":[{\"tag\":\"标签名\",\"confidence\":0.0-1.0}]}。");
        sb.Append("\n- 只能使用词表中列出的标签，不要编造。");
        sb.Append("\n- 每组最多选 3 个最匹配的标签，confidence 为匹配置信度（0.0-1.0）。");
        sb.Append("\n- 不要输出 category 字段；分类由本地根据标签推导。");
        sb.Append("\n- 只输出 JSON，无多余文字。\n\n");
        sb.Append("标签词表（按组）：\n");

        foreach (string group in TagTaxonomy.AllGroups)
        {
            // 文件格式由本地从图片元数据识别；色彩倾向走色板提取。
            if (group == TagTaxonomy.GroupFileFormat || group == TagTaxonomy.GroupColorTone)
                continue;
            if (!TagTaxonomy.TagsByGroup.TryGetValue(group, out var tags) || tags.Count == 0)
                continue;
            sb.Append(group).Append("：").Append(string.Join(", ", tags)).Append('\n');
        }

        sb.Append("\n文件格式由本地从图片元数据自动识别，无需输出。");
        sb.Append("\n色彩倾向由本地色板提取，无需输出。");
        return sb.ToString();
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl("models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DecryptKey());
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        if (!IsAvailable)
            return new AiAnalysisResult { ErrorMessage = "云端 AI 未配置完成。" };

        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            string base64 = Convert.ToBase64String(imageBytes);
            string mime = GuessMime(imagePath);
            string dataUrl = $"data:{mime};base64,{base64}";

            // ── Tagging via chat/completions ──────────────────────────────
            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content = SystemPrompt,
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            };

            var payload = new
            {
                model = _config.ModelName,
                messages,
                response_format = new { type = "json_object" },
                max_tokens = 400,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl("chat/completions"))
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DecryptKey());

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            string content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;

            var parsed = JsonSerializer.Deserialize<JsonElement>(content);

            var tags = new List<AiTag>();
            if (parsed.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    string? tag = t.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : null;
                    double conf = t.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out double c) ? c : 0.0;
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(new AiTag(tag.Trim(), conf));
                }
            }

            // ── Optional embedding ────────────────────────────────────────
            float[]? embedding = null;
            if (SupportsEmbeddings)
            {
                try
                {
                    string input = string.Join(" ", tags.Select(t => t.Tag));
                    embedding = await GetEmbeddingAsync(input, cancellationToken);
                }
                catch
                {
                    // Embedding failure does not fail the whole analysis.
                }
            }

            // M12-D: category is derived from tags downstream (AiAnalysisService
            // calls TaxonomyService.DeriveCategory). Cloud no longer outputs it.
            return new AiAnalysisResult
            {
                PrimaryCategory = null,
                Tags = tags,
                Embedding = embedding,
                ModelId = _config.ModelName,
            };
        }
        catch (Exception ex)
        {
            return new AiAnalysisResult
            {
                ErrorMessage = $"云端分析失败：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// Computes a text embedding for semantic search via the /embeddings endpoint.
    /// Returns null on any failure (no network, auth, config) — never throws.
    /// </summary>
    public async Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsAvailable || !SupportsEmbeddings) return null;
        try { return await GetEmbeddingAsync(text, cancellationToken); }
        catch { return null; }
    }

    private async Task<float[]?> GetEmbeddingAsync(string input, CancellationToken ct)
    {
        var payload = new { model = _config.EmbeddingModelName, input };
        using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl("embeddings"))
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DecryptKey());

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var vec = json.GetProperty("data")[0].GetProperty("embedding");
        var result = new List<float>();
        foreach (var v in vec.EnumerateArray())
            result.Add(v.GetSingle());
        return [.. result];
    }

    /// <summary>
    /// 0.16.0 DUP-E: 双图 VLM 终审。失败（未配置/网络/解析）返回 (false, 0)，
    /// 调用方把 Confidence&lt;=0 视为「无判定」，不会误丢弃候选对。
    /// </summary>
    public async Task<(bool SameImage, double Confidence)> CompareImagesAsync(
        string pathA, string pathB, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pathA);
        ArgumentNullException.ThrowIfNull(pathB);
        if (!IsAvailable)
            return (false, 0);

        try
        {
            string dataUrlA = await ToDataUrlAsync(pathA, cancellationToken);
            string dataUrlB = await ToDataUrlAsync(pathB, cancellationToken);

            var messages = new object[]
            {
                new { role = "system", content = VlmComparePrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "判断这两张图片是否同一画面。" },
                        new { type = "image_url", image_url = new { url = dataUrlA } },
                        new { type = "image_url", image_url = new { url = dataUrlB } },
                    },
                },
            };

            var payload = new
            {
                model = _config.ModelName,
                messages,
                response_format = new { type = "json_object" },
                max_tokens = 100,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl("chat/completions"))
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DecryptKey());

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            string content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            var parsed = JsonSerializer.Deserialize<JsonElement>(content);

            if (!parsed.TryGetProperty("same", out JsonElement sameElement) ||
                sameElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (false, 0);

            double confidence =
                parsed.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                confidenceElement.TryGetDouble(out double value)
                    ? Math.Clamp(value, 0, 1)
                    : 0;
            return (sameElement.GetBoolean(), confidence);
        }
        catch
        {
            return (false, 0);
        }
    }

    private static async Task<string> ToDataUrlAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return $"data:{GuessMime(path)};base64,{Convert.ToBase64String(bytes)}";
    }

    private string NormalizeUrl(string endpoint)
    {
        string baseUrl = _config.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/{endpoint}";
    }

    private string DecryptKey()
    {
        // Decryption is expected to succeed because IsAvailable checked it.
        return QL.World.Interop.DpApiProtector.Unprotect(_config.EncryptedApiKey) ?? string.Empty;
    }

    private static string GuessMime(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }
}
