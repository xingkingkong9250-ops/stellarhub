using System.Net.Http;
using QL.World.Models;
using QL.World.Services.Ai;
using QL.World.Services.Ai.Local;

namespace QL.World.Services;

/// <summary>
/// Creates the appropriate <see cref="IAiService"/> based on the configured
/// AI backend. v0.12.0: multi-backend architecture — the default overload reads
/// <see cref="AppSettingsService.ActiveAiBackendId"/>; an explicit
/// <see cref="Create(AppSettingsService, int, HttpMessageHandler?, Action{string}?)"/>
/// overload resolves by backend id. The legacy
/// <see cref="Create(AppSettingsService, AiMode, HttpMessageHandler?, Action{string}?)"/>
/// overload is retained for backward-compat tests.
/// Cloud services receive an injectable <see cref="HttpMessageHandler"/> so tests
/// can mock network responses; the local service is always constructible
/// (IsAvailable=false until the model file exists, which also greys out the AI
/// entry points).
/// </summary>
public static class AiServiceFactory
{
    /// <summary>
    /// v0.12.0: 默认重载——读 <see cref="AppSettingsService.ActiveAiBackendId"/>。
    /// 有值→按 id 创建；null→<see cref="DisabledAiService"/>。
    /// </summary>
    public static IAiService Create(
        AppSettingsService settings,
        HttpMessageHandler? handler = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int? activeId = settings.ActiveAiBackendId;
        return activeId is { } id
            ? Create(settings, id, handler, log)
            : new DisabledAiService();
    }

    /// <summary>
    /// v0.12.0: 按 backend id 创建——查 backend → Kind=Local 走
    /// <see cref="LocalAiService"/>（传 modelPath）；Kind=Cloud 走
    /// <see cref="CloudAiService"/>。Id 不存在→<see cref="DisabledAiService"/>。
    /// </summary>
    public static IAiService Create(
        AppSettingsService settings,
        int backendId,
        HttpMessageHandler? handler = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AiBackendConfig? backend = settings.GetAiBackend(backendId);
        if (backend is null)
            return new DisabledAiService();

        return backend.Kind switch
        {
            AiBackendKind.Cloud => new CloudAiService(BuildCloudConfig(backend), handler),
            AiBackendKind.Local => new LocalAiService(settings, log, modelPath: backend.ModelPath),
            _ => new DisabledAiService(),
        };
    }

    /// <summary>
    /// v0.9.0 (W2/AI): 按显式指定模式创建后端——导入对话框按次选择
    /// 云端/本地时使用，不改写全局设置。v0.12.0 保留供旧测试与
    /// <see cref="AiAnalysisService"/> 的 backendResolver（Phase 3 演进为 id）。
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

    /// <summary>从旧 <see cref="AppSettingsService"/> 的单组 CloudAi 字段构建配置（legacy 重载用）。</summary>
    private static CloudAiConfig BuildCloudConfig(AppSettingsService settings) =>
        new()
        {
            BaseUrl = settings.CloudBaseUrl,
            ModelName = settings.CloudModelName,
            EncryptedApiKey = settings.CloudEncryptedApiKey,
            EmbeddingModelName = settings.CloudEmbeddingModelName,
        };

    /// <summary>v0.12.0: 从 <see cref="AiBackendConfig"/> 构建 Cloud 配置。</summary>
    private static CloudAiConfig BuildCloudConfig(AiBackendConfig backend) =>
        new()
        {
            BaseUrl = backend.BaseUrl,
            ModelName = backend.ModelName,
            EncryptedApiKey = backend.EncryptedApiKey,
            EmbeddingModelName = backend.EmbeddingModelName,
        };
}
