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
    public void Local_mode_returns_local_service()
    {
        IAiService ai = AiServiceFactory.Create(MakeSettings(AiMode.Local));
        Assert.IsType<LocalAiService>(ai);
        Assert.Equal("Local", ai.Mode);
        Assert.True(ai.SupportsEmbeddings);
        // 模型已打包到输出目录 models\ → IsAvailable 跟随打包位置。
        // 若打包位置无模型文件则 IsAvailable=false（开发环境可能如此）。
        // 此处只验证类型与模式，不硬断言 IsAvailable（取决于打包位置是否存在模型）。
    }
}
