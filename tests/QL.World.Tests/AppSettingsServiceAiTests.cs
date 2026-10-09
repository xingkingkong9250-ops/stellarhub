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
    public void Default_ai_mode_is_local_for_fresh_install()
    {
        // v0.12.0: 新装自动种子内置 Local backend 并设为活跃（即用）。
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Equal(AiMode.Local, settings.AiMode);
        Assert.NotNull(settings.ActiveAiBackendId);
        Assert.Single(settings.AiBackends);
        Assert.True(settings.AiBackends[0].IsBundled);
    }

    [Fact]
    public void Active_backend_persists_across_instances()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        int id = first.AddAiBackend(new AiBackendConfig
        {
            Name = "my-cloud",
            Kind = AiBackendKind.Cloud,
            BaseUrl = "https://api.openai.com/v1",
            ModelName = "gpt-4o",
        });
        first.ActiveAiBackendId = id;
        Assert.Equal(AiMode.Cloud, first.AiMode);

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Equal(AiMode.Cloud, second.AiMode);
        Assert.Equal(id, second.ActiveAiBackendId);
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

    // ─── v0.12.0: multi-backend CRUD + migration ──────────────────────────

    [Fact]
    public void DeleteAiBackend_refuses_bundled()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        // Id=1 is the bundled backend.
        Assert.Throws<InvalidOperationException>(() => s.DeleteAiBackend(1));
        // Bundled backend still present.
        Assert.Single(s.AiBackends);
        Assert.True(s.AiBackends[0].IsBundled);
    }

    [Fact]
    public void DeleteAiBackend_clears_active_id_when_active_deleted()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        int id = s.AddAiBackend(new AiBackendConfig
        {
            Name = "temp-cloud",
            Kind = AiBackendKind.Cloud,
            BaseUrl = "https://api.openai.com/v1",
        });
        s.ActiveAiBackendId = id;
        Assert.Equal(AiMode.Cloud, s.AiMode);

        s.DeleteAiBackend(id);
        Assert.Null(s.ActiveAiBackendId);
        Assert.Equal(AiMode.Disabled, s.AiMode);
    }

    [Fact]
    public void Backend_api_key_is_dpapi_encrypted()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        int id = s.AddAiBackend(new AiBackendConfig
        {
            Name = "cloud",
            Kind = AiBackendKind.Cloud,
            BaseUrl = "https://api.openai.com/v1",
        });

        const string key = "sk-backend-secret-999";
        s.SetAiBackendApiKey(id, key);

        Assert.True(s.HasAiBackendApiKey(id));
        Assert.Equal(key, s.GetAiBackendApiKey(id));

        // config.json must NOT contain the plaintext key.
        string json = File.ReadAllText(Path.Combine(_directory, "config.json"));
        Assert.DoesNotContain(key, json);
    }

    [Fact]
    public void Clear_backend_api_key_works()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        int id = s.AddAiBackend(new AiBackendConfig
        {
            Name = "cloud",
            Kind = AiBackendKind.Cloud,
        });
        s.SetAiBackendApiKey(id, "sk-temp");
        Assert.True(s.HasAiBackendApiKey(id));

        s.SetAiBackendApiKey(id, null);
        Assert.False(s.HasAiBackendApiKey(id));
        Assert.Null(s.GetAiBackendApiKey(id));
    }

    [Fact]
    public void Legacy_cloud_config_migrates_to_backend_on_load()
    {
        // 旧 config: AiMode=Cloud + CloudAi 有实质内容 → 迁移为 Cloud backend。
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"aiMode\":2,\"cloudAi\":{\"baseUrl\":\"https://api.openai.com/v1\",\"modelName\":\"gpt-4o\"}}");

        var s = new AppSettingsService(_directory);
        s.Load();

        // 内置 Local(Id=1) + 迁移的 Cloud(Id=2)
        Assert.Equal(2, s.AiBackends.Count);
        Assert.True(s.AiBackends[0].IsBundled);
        Assert.Equal(AiBackendKind.Cloud, s.AiBackends[1].Kind);
        Assert.Equal("gpt-4o", s.AiBackends[1].Name);
        Assert.Equal("https://api.openai.com/v1", s.AiBackends[1].BaseUrl);
        // 旧 AiMode=Cloud → active=2
        Assert.Equal(2, s.ActiveAiBackendId);
        Assert.Equal(AiMode.Cloud, s.AiMode);
    }

    [Fact]
    public void Legacy_disabled_mode_stays_disabled_on_upgrade()
    {
        // 旧 config: AiMode=Disabled(0) → 升级后 active=null
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"aiMode\":0}");

        var s = new AppSettingsService(_directory);
        s.Load();

        // 内置 Local 已种子，但 active=null（尊重旧 Disabled）
        Assert.Single(s.AiBackends);
        Assert.Null(s.ActiveAiBackendId);
        Assert.Equal(AiMode.Disabled, s.AiMode);
    }

    [Fact]
    public void Orphan_active_id_falls_back_to_disabled()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        // Active 指向不存在的 backend → 归零
        s.ActiveAiBackendId = 9999;
        // 重新加载触发 NormalizeAiBackends 校验
        var s2 = new AppSettingsService(_directory);
        s2.Load();
        Assert.Null(s2.ActiveAiBackendId);
        Assert.Equal(AiMode.Disabled, s2.AiMode);
    }
}

