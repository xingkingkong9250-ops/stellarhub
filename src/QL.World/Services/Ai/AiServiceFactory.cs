using System.Net.Http;
using QL.World.Models;
using QL.World.Services.Ai;

namespace QL.World.Services;

/// <summary>
/// Creates the appropriate <see cref="IAiService"/> based on the configured
/// <see cref="AiMode"/>. Cloud/local services receive an injectable
/// <see cref="HttpMessageHandler"/> so tests can mock network responses.
/// </summary>
public static class AiServiceFactory
{
    public static IAiService Create(AppSettingsService settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.AiMode switch
        {
            AiMode.Cloud => new CloudAiService(BuildCloudConfig(settings), handler),
            AiMode.Local => throw new NotSupportedException("本地 ONNX 推理将在 M3-D 提供。"),
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
