using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Ai.Local;

namespace QL.World.Tests.Ai;

public sealed class AiServiceFactoryTests : IDisposable
{
    private readonly string _directory;

    public AiServiceFactoryTests()
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
        catch (IOException) { }
    }

    private AppSettingsService MakeSettings()
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        return s;
    }

    [Fact]
    public void Default_overload_returns_local_for_fresh_install()
    {
        // v0.12.0: 新装种子内置 Local backend 为活跃。
        IAiService ai = AiServiceFactory.Create(MakeSettings());
        Assert.IsType<LocalAiService>(ai);
        Assert.Equal("Local", ai.Mode);
        Assert.True(ai.SupportsEmbeddings);
    }

    [Fact]
    public void Default_overload_returns_disabled_when_no_active_backend()
    {
        var s = MakeSettings();
        s.ActiveAiBackendId = null;
        IAiService ai = AiServiceFactory.Create(s);
        Assert.IsType<DisabledAiService>(ai);
    }

    [Fact]
    public void Default_overload_returns_cloud_for_active_cloud_backend()
    {
        var s = MakeSettings();
        int id = s.AddAiBackend(new AiBackendConfig
        {
            Name = "test-cloud",
            Kind = AiBackendKind.Cloud,
            BaseUrl = "https://api.openai.com/v1",
        });
        s.ActiveAiBackendId = id;

        IAiService ai = AiServiceFactory.Create(s);
        Assert.IsType<CloudAiService>(ai);
    }

    [Fact]
    public void Create_by_backend_id_resolves_correct_service()
    {
        var s = MakeSettings();
        // Bundled Local backend is Id=1.
        IAiService local = AiServiceFactory.Create(s, 1);
        Assert.IsType<LocalAiService>(local);

        int cloudId = s.AddAiBackend(new AiBackendConfig
        {
            Name = "cloud",
            Kind = AiBackendKind.Cloud,
            BaseUrl = "https://api.openai.com/v1",
        });
        IAiService cloud = AiServiceFactory.Create(s, cloudId);
        Assert.IsType<CloudAiService>(cloud);

        // Unknown id → Disabled.
        IAiService unknown = AiServiceFactory.Create(s, 9999);
        Assert.IsType<DisabledAiService>(unknown);
    }

    [Fact]
    public void Legacy_mode_overload_still_works()
    {
        // v0.12.0: 旧 Create(settings, mode) 重载保留供旧测试与 backendResolver。
        var s = MakeSettings();

        IAiService disabled = AiServiceFactory.Create(s, AiMode.Disabled);
        Assert.IsType<DisabledAiService>(disabled);

        IAiService local = AiServiceFactory.Create(s, AiMode.Local);
        Assert.IsType<LocalAiService>(local);

        // CloudAi config is empty but CloudAiService is still constructible.
        IAiService cloud = AiServiceFactory.Create(s, AiMode.Cloud);
        Assert.IsType<CloudAiService>(cloud);
    }
}
