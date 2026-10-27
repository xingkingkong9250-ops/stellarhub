using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QL.World.Interop;
using QL.World.Models;
using QL.World.Services.Ai;

namespace QL.World.Tests.Ai;

/// <summary>
/// HttpMessageHandler that returns a canned response for testing CloudAiService
/// without hitting the network.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_responder(request));
}

public sealed class CloudAiServiceTests
{
    private static CloudAiConfig MakeConfig() =>
        new()
        {
            BaseUrl = "https://api.example.com/v1",
            ModelName = "test-model",
            EncryptedApiKey = DpApiProtector.Protect("sk-test-key")!,
            EmbeddingModelName = "test-embed",
        };

    [Fact]
    public void IsAvailable_true_when_configured()
    {
        var ai = new CloudAiService(MakeConfig());
        Assert.True(ai.IsAvailable);
        Assert.True(ai.SupportsEmbeddings);
    }

    [Fact]
    public void IsAvailable_false_when_missing_key()
    {
        var config = MakeConfig();
        config.EncryptedApiKey = string.Empty;
        var ai = new CloudAiService(config);
        Assert.False(ai.IsAvailable);
    }

    [Fact]
    public async Task TestConnection_success_returns_true()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        var ai = new CloudAiService(MakeConfig(), handler);

        Assert.True(await ai.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TestConnection_unauthorized_returns_false()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ai = new CloudAiService(MakeConfig(), handler);

        Assert.False(await ai.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TestConnection_sends_bearer_token()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var ai = new CloudAiService(MakeConfig(), handler);

        await ai.TestConnectionAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Contains("models", captured.RequestUri!.ToString());
        Assert.StartsWith("Bearer ", captured.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task AnalyzeImage_parses_tags_only()
    {
        // M12-D: cloud no longer outputs category; derived from tags downstream.
        string json = "{\"tags\":[{\"tag\":\"金属\",\"confidence\":0.92},{\"tag\":\"骑士\",\"confidence\":0.78}]}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"" + json.Replace("\"", "\\\"") + "\"}}]}"),
        };
        var handler = new StubHttpHandler(_ => response);
        var ai = new CloudAiService(MakeConfig(), handler);

        // Create a tiny temp image file.
        string path = Path.Combine(Path.GetTempPath(), $"ql-ai-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71, 13, 10, 26, 10]);

        try
        {
            var result = await ai.AnalyzeImageAsync(path, CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Null(result.PrimaryCategory);
            Assert.Equal(2, result.Tags.Count);
            Assert.Equal("金属", result.Tags[0].Tag);
            Assert.Equal(0.92, result.Tags[0].Confidence, 2);
            Assert.Equal("骑士", result.Tags[1].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnalyzeImage_system_prompt_lists_new_taxonomy_tags()
    {
        // M12-D: system prompt is built from TagTaxonomy — must list new 6
        // category tags and omit old categories (概念图/UI图标/etc.).
        string? capturedBody = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"tags\\\":[]}\"}}]}"),
        };
        var handler = new StubHttpHandler(req =>
        {
            // Read inside the handler — the request is disposed after SendAsync returns.
            // Capture only the first (chat/completions) request, not embeddings.
            capturedBody ??= req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return response;
        });
        var ai = new CloudAiService(MakeConfig(), handler);

        string path = Path.Combine(Path.GetTempPath(), $"ql-ai-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71, 13, 10, 26, 10]);

        try
        {
            await ai.AnalyzeImageAsync(path, CancellationToken.None);

            Assert.NotNull(capturedBody);
            // JSON escapes non-ASCII as \uXXXX — parse to get the decoded content.
            using JsonDocument doc = JsonDocument.Parse(capturedBody);
            string systemContent = doc.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString()!;
            Assert.Contains("骑士", systemContent);      // 角色 group
            Assert.Contains("森林", systemContent);      // 场景 group
            Assert.Contains("金属", systemContent);      // 质感 group
            Assert.DoesNotContain("概念图", systemContent);  // old category removed
            Assert.DoesNotContain("UI图标", systemContent);  // old category removed
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnalyzeImage_network_error_returns_error_result()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("boom"));
        var ai = new CloudAiService(MakeConfig(), handler);

        string path = Path.Combine(Path.GetTempPath(), $"ql-ai-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71, 13, 10, 26, 10]);

        try
        {
            var result = await ai.AnalyzeImageAsync(path, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("失败", result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmbedText_returns_vector_on_success()
    {
        string json = "{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        var handler = new StubHttpHandler(_ => response);
        var ai = new CloudAiService(MakeConfig(), handler);

        float[]? emb = await ai.EmbedTextAsync("硬表面金属质感", CancellationToken.None);

        Assert.NotNull(emb);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, emb!.ToArray());
    }

    [Fact]
    public async Task EmbedText_returns_null_on_http_error()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var ai = new CloudAiService(MakeConfig(), handler);

        float[]? emb = await ai.EmbedTextAsync("test", CancellationToken.None);
        Assert.Null(emb);
    }

    [Fact]
    public async Task EmbedText_returns_null_when_embeddings_not_configured()
    {
        var config = MakeConfig();
        config.EmbeddingModelName = string.Empty;
        var ai = new CloudAiService(config);

        float[]? emb = await ai.EmbedTextAsync("test", CancellationToken.None);
        Assert.Null(emb);
    }

    // ── 0.16.0 DUP-E: 双图 VLM 对比 ───────────────────────────────────

    [Fact]
    public async Task CompareImages_parses_same_and_confidence()
    {
        string json = "{\"same\":true,\"confidence\":0.87}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"" + json.Replace("\"", "\\\"") + "\"}}]}"),
        };
        var ai = new CloudAiService(MakeConfig(), new StubHttpHandler(_ => response));

        (string a, string b) = await CreatePairFilesAsync();
        try
        {
            (bool same, double confidence) = await ai.CompareImagesAsync(
                a, b, CancellationToken.None);

            Assert.True(same);
            Assert.Equal(0.87, confidence, 3);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task CompareImages_sends_both_images_in_request()
    {
        string? capturedBody = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"same\\\":false,\\\"confidence\\\":0.9}\"}}]}"),
        };
        var handler = new StubHttpHandler(request =>
        {
            capturedBody ??= request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return response;
        });
        var ai = new CloudAiService(MakeConfig(), handler);

        (string a, string b) = await CreatePairFilesAsync();
        try
        {
            await ai.CompareImagesAsync(a, b, CancellationToken.None);

            Assert.NotNull(capturedBody);
            using JsonDocument doc = JsonDocument.Parse(capturedBody);
            JsonElement content = doc.RootElement
                .GetProperty("messages")[1]
                .GetProperty("content");
            int imageCount = content.EnumerateArray()
                .Count(item => item.GetProperty("type").GetString() == "image_url");
            Assert.Equal(2, imageCount);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task CompareImages_returns_no_verdict_when_not_configured()
    {
        var config = MakeConfig();
        config.EncryptedApiKey = string.Empty;
        var ai = new CloudAiService(config);

        (bool same, double confidence) = await ai.CompareImagesAsync(
            "a.png", "b.png", CancellationToken.None);

        Assert.False(same);
        Assert.Equal(0, confidence);
    }

    [Fact]
    public async Task CompareImages_returns_no_verdict_on_http_error()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var ai = new CloudAiService(MakeConfig(), handler);

        (string a, string b) = await CreatePairFilesAsync();
        try
        {
            (bool same, double confidence) = await ai.CompareImagesAsync(
                a, b, CancellationToken.None);

            Assert.False(same);
            Assert.Equal(0, confidence);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    private static async Task<(string A, string B)> CreatePairFilesAsync()
    {
        string a = Path.Combine(Path.GetTempPath(), $"ql-vlm-a-{Guid.NewGuid():N}.png");
        string b = Path.Combine(Path.GetTempPath(), $"ql-vlm-b-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(a, [137, 80, 78, 71, 13, 10, 26, 10]);
        await File.WriteAllBytesAsync(b, [137, 80, 78, 71, 13, 10, 26, 10]);
        return (a, b);
    }
}
