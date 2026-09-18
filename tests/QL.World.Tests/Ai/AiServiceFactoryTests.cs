using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;

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

    private AppSettingsService MakeSettings(AiMode mode)
    {
        var s = new AppSettingsService(_directory);
        s.Load();
        s.AiMode = mode;
        return s;
    }

    [Fact]
    public void Disabled_mode_returns_disabled_service()
    {
        IAiService ai = AiServiceFactory.Create(MakeSettings(AiMode.Disabled));
        Assert.IsType<DisabledAiService>(ai);
    }

    [Fact]
    public void Cloud_mode_returns_cloud_service()
    {
        IAiService ai = AiServiceFactory.Create(MakeSettings(AiMode.Cloud));
        Assert.IsType<CloudAiService>(ai);
    }

    [Fact]
    public void Local_mode_throws_not_supported()
    {
        Assert.Throws<NotSupportedException>(
            () => AiServiceFactory.Create(MakeSettings(AiMode.Local)));
    }
}
