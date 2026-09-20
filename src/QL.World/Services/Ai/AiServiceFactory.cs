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

        return settings.AiMode switch
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
