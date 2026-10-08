using System.Net.Http;
using QL.World.Models;
using QL.World.Services.Ai;
using QL.World.Services.Ai.Local;

namespace QL.World.Services;

/// <summary>
/// Creates the appropriate <see cref="IAiService"/> based on the configured
/// <see cref="AiMode"/>. Cloud services receive an injectable
/// <see cref="HttpMessageHandler"/> so tests can mock network responses; the
/// local service is always constructible (IsAvailable=false until the model
/// file exists, which also greys out the AI entry points).
/// </summary>
public static class AiServiceFactory
{
    public static IAiService Create(
        AppSettingsService settings,
        HttpMessageHandler? handler = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Create(settings, settings.AiMode, handler, log);
    }

    /// <summary>
    /// v0.9.0 (W2/AI): 按显式指定模式创建后端——导入对话框按次选择
    /// 云端/本地时使用，不改写全局设置。
    /// </summary>
    public static IAiService Create(
        AppSettingsService settings,
        AiMode mode,
        HttpMessageHandler? handler = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return mode switch
        {
            AiMode.Cloud => new CloudAiService(BuildCloudConfig(settings), handler),
            AiMode.Local => new LocalAiService(settings, log),
            _ => new DisabledAiService(),
        };
    }

    private static CloudAiConfig BuildCloudConfig(AppSettingsService settings) =>
        new()
        {
            BaseUrl = settings.CloudBaseUrl,
            ModelName = settings.CloudModelName,
            EncryptedApiKey = settings.CloudEncryptedApiKey,
            EmbeddingModelName = settings.CloudEmbeddingModelName,
        };
}
