using System.IO;
using System.Text.Json;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class AppSettingsServiceAiTests : IDisposable
{
    private readonly string _directory;

    public AppSettingsServiceAiTests()
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
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }

    [Fact]
    public void Default_ai_mode_is_disabled()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Equal(AiMode.Disabled, settings.AiMode);
    }

    [Fact]
    public void Ai_mode_persists_across_instances()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        first.AiMode = AiMode.Cloud;

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Equal(AiMode.Cloud, second.AiMode);
    }

    [Fact]
    public void Invalid_ai_mode_falls_back_to_disabled()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"aiMode\":99}");

        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Equal(AiMode.Disabled, settings.AiMode);
    }

    [Fact]
    public void Cloud_base_url_rejects_non_https()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();

        settings.CloudBaseUrl = "http://api.example.com";
        Assert.Equal(string.Empty, settings.CloudBaseUrl);

        settings.CloudBaseUrl = "ftp://api.example.com";
        Assert.Equal(string.Empty, settings.CloudBaseUrl);
    }

    [Fact]
    public void Cloud_base_url_accepts_https()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();

        settings.CloudBaseUrl = "https://api.openai.com/v1";
        Assert.Equal("https://api.openai.com/v1", settings.CloudBaseUrl);
    }

    [Fact]
    public void Api_key_is_encrypted_not_stored_in_plaintext()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();

        const string key = "sk-secret-plaintext-12345";
        settings.SetCloudApiKey(key);

        Assert.True(settings.HasCloudApiKey);

        // config.json must NOT contain the plaintext key.
        string json = File.ReadAllText(Path.Combine(_directory, "config.json"));
        Assert.DoesNotContain(key, json);

        // The stored value should be Base64 (DPAPI ciphertext).
        var doc = JsonDocument.Parse(json);
        string encrypted = doc.RootElement.GetProperty("cloudAi").GetProperty("encryptedApiKey").GetString()!;
        Assert.NotEqual(key, encrypted);
        // Round-trip: decrypt returns the original.
        Assert.Equal(key, settings.GetCloudApiKey());
    }

    [Fact]
    public void GetCloudApiKey_returns_null_when_not_set()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Null(settings.GetCloudApiKey());
    }

    [Fact]
    public void ClearCloudApiKey_removes_stored_key()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        settings.SetCloudApiKey("sk-temp");
        Assert.True(settings.HasCloudApiKey);

        settings.ClearCloudApiKey();
        Assert.False(settings.HasCloudApiKey);
        Assert.Null(settings.GetCloudApiKey());
    }

    [Fact]
    public void Cloud_model_name_persists()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        first.CloudModelName = "gpt-4o-mini";

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Equal("gpt-4o-mini", second.CloudModelName);
    }

    [Fact]
    public void Non_https_base_url_is_cleared_on_load()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"cloudAi\":{\"baseUrl\":\"http://evil.com\"}}");

        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Equal(string.Empty, settings.CloudBaseUrl);
    }
}
