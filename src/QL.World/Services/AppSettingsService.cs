using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QL.World.Interop;
using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;

namespace QL.World.Services;

/// <summary>
/// Strongly typed application settings persisted as config.json in the
/// QL World data root. Writes go through AtomicTextFile; a corrupt primary
/// file falls back to the .backup recovery snapshot.
/// </summary>
public sealed class AppSettingsService
{
    private const int MIN_THUMBNAIL_SIZE = 128;
    private const int MAX_THUMBNAIL_SIZE = 512;
    private const int MIN_THUMBNAIL_QUALITY = 30;
    private const int MAX_THUMBNAIL_QUALITY = 100;
    private const int MIN_ZOOM = 80;
    private const int MAX_ZOOM = 512;
    private const int MIN_THUMBNAIL_CACHE_MB = 128;
    private const int MAX_THUMBNAIL_CACHE_MB = 8192;

    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _settingsPath;
    private readonly string _dataRoot;
    private readonly Action<string> _log;
    private readonly bool _settingsExistedAtStartup;
    private AppSettings _settings = new();

    public AppSettingsService(string dataRoot, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
        _settingsPath = Path.Combine(dataRoot, AppDataRootResolver.ConfigFileName);
        _log = log ?? (_ => { });
        _settingsExistedAtStartup =
            File.Exists(_settingsPath) ||
            File.Exists(AtomicTextFile.GetBackupPath(_settingsPath));
        // v0.12.0: 构造后即种子内置 backend，保证 Load() 前也可正常读写 AiMode。
        NormalizeAiBackends();
    }

    /// <summary>The QL World data root this settings file lives in (e.g. for the models directory).</summary>
    public string DataRoot =>
        string.IsNullOrWhiteSpace(_settings.DataRootPath)
            ? _dataRoot
            : _settings.DataRootPath!;

    public event Action? Changed;

    public bool LastSaveSucceeded { get; private set; } = true;

    public bool IsNewInstallation => !_settingsExistedAtStartup;

    // ─── Properties ───────────────────────────────────────────────────────

    public string Theme
    {
        get => _settings.Theme;
        set => SetAndSave(value,
            () => _settings.Theme,
            next => _settings.Theme = next);
    }

    public bool CheckIntegrityOnStartup
    {
        get => _settings.CheckIntegrityOnStartup;
        set => SetAndSave(value,
            () => _settings.CheckIntegrityOnStartup,
            next => _settings.CheckIntegrityOnStartup = next);
    }

    public int ThumbnailSize
    {
        get => _settings.ThumbnailSize;
        set => SetAndSave(
            Math.Clamp(value, MIN_THUMBNAIL_SIZE, MAX_THUMBNAIL_SIZE),
            () => _settings.ThumbnailSize,
            next => _settings.ThumbnailSize = next);
    }

    public int ThumbnailQuality
    {
        get => _settings.ThumbnailQuality;
        set => SetAndSave(
            Math.Clamp(value, MIN_THUMBNAIL_QUALITY, MAX_THUMBNAIL_QUALITY),
            () => _settings.ThumbnailQuality,
            next => _settings.ThumbnailQuality = next);
    }

    /// <summary>M5-C: thumbnail disk-cache cap in MB; clamped to [128, 8192] on set/load.</summary>
    public int ThumbnailCacheLimitMb
    {
        get => _settings.ThumbnailCacheLimitMb;
        set => SetAndSave(
            Math.Clamp(value, MIN_THUMBNAIL_CACHE_MB, MAX_THUMBNAIL_CACHE_MB),
            () => _settings.ThumbnailCacheLimitMb,
            next => _settings.ThumbnailCacheLimitMb = next);
    }

    /// <summary>
    /// M8-E: 是否在共享画廊显示数据来源徽章（nas://{id}）。默认 false。
    /// 管理员可在 A7 设置开关；开启后每张共享图带 nas:// 来源标识。
    /// </summary>
    public bool ShowSourceBadge
    {
        get => _settings.ShowSourceBadge;
        set => SetAndSave(value,
            () => _settings.ShowSourceBadge,
            next => _settings.ShowSourceBadge = next);
    }

    // ─── M11: 软件托管图片库 ──────────────────────────────────────────────

    /// <summary>M11: 软件托管图片库根目录；null=W7 向导尚未配置。</summary>
    public string? LibraryRootPath => _settings.LibraryRootPath;

    /// <summary>M11: W7 向导是否已完成库目录选择。</summary>
    public bool IsLibraryRootConfigured =>
        !string.IsNullOrWhiteSpace(_settings.LibraryRootPath);

    /// <summary>
    /// 用户选定的统一数据与图片存储目录。null = 回落到固定 %APPDATA% configRoot。
    /// 与 LibraryRootPath 同时赋值且相等（W7 向导保证）。
    /// </summary>
    public string? DataRootPath
    {
        get => _settings.DataRootPath;
        set => SetAndSave(value,
            () => _settings.DataRootPath,
            next => _settings.DataRootPath = next);
    }

    /// <summary>
    /// M11: 由 W7 向导调用，持久化用户选择的库目录（规范化为绝对路径）。
    /// 同时写 DataRootPath（两者相等，数据和图片统一路径）。
    /// </summary>
    public void ConfigureLibraryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path.Trim());
        SetAndSave(full,
            () => _settings.LibraryRootPath,
            next => _settings.LibraryRootPath = next);
        // 同步 DataRootPath（若不同则持久化第二次）
        if (_settings.DataRootPath != full)
            SetAndSave(full,
                () => _settings.DataRootPath,
                next => _settings.DataRootPath = next);
    }

    /// <summary>M11: 老用户在 W7 选择「跳过」迁移的软跳过标志位。</summary>
    public bool LegacyDataSkipped
    {
        get => _settings.LegacyDataSkipped;
        set => SetAndSave(value,
            () => _settings.LegacyDataSkipped,
            next => _settings.LegacyDataSkipped = next);
    }

    /// <summary>
    /// M11: W3/A1「显示未托管图」开关（默认 false）。专用事件（而非通用
    /// Changed）让画廊只在该开关翻转时重查，避免其他设置项引发多余刷新。
    /// </summary>
    public event Action? LegacyVisibilityChanged;

    public bool ShowLegacyImages
    {
        get => _settings.ShowLegacyImages;
        set
        {
            bool previous = _settings.ShowLegacyImages;
            if (previous == value) return;
            _settings.ShowLegacyImages = value;
            if (TrySaveNow())
            {
                Changed?.Invoke();
                LegacyVisibilityChanged?.Invoke();
            }
            else
            {
                _settings.ShowLegacyImages = previous;
            }
        }
    }

    /// <summary>
    /// v0.10.0: AI 自适应学习开关（W3/A3）。默认 true。
    /// 关闭后本地分析跳过学习精排，训练数据快照仍写入（留存最大化）。
    /// </summary>
    public bool AiLearningEnabled
    {
        get => _settings.AiLearningEnabled;
        set => SetAndSave(value,
            () => _settings.AiLearningEnabled,
            next => _settings.AiLearningEnabled = next);
    }

    // ─── Login credentials (W5) ───────────────────────────────────────────

    /// <summary>W5-4: 记住的登录用户名（明文，用户名本身非敏感）；null=未记住。</summary>
    public string? RememberedLoginUsername
    {
        get => _settings.RememberedLoginUsername;
        set => SetAndSave(value,
            () => _settings.RememberedLoginUsername,
            next => _settings.RememberedLoginUsername = next);
    }

    /// <summary>
    /// W5-5: 加密并持久化记住的登录密码。null/空串清除记录；DPAPI 失败时
    /// 不写入并记日志。明文不出现在 config.json 或日志中。
    /// </summary>
    public void SetRememberedLoginPassword(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            SetAndSave<string?>(null,
                () => _settings.RememberedLoginPassword,
                next => _settings.RememberedLoginPassword = next);
            return;
        }
        string? encrypted = DpApiProtector.Protect(plaintext);
        if (encrypted is null)
        {
            _log("[SETTINGS] DPAPI protect failed; remembered login password not saved.");
            return;
        }
        SetAndSave(encrypted,
            () => _settings.RememberedLoginPassword,
            next => _settings.RememberedLoginPassword = next);
    }

    /// <summary>
    /// W5-5: 解密返回记住的登录密码明文，未记录或解密失败返回 null。
    /// 调用方绝不能记录返回值。
    /// </summary>
    public string? GetRememberedLoginPassword()
        => DpApiProtector.Unprotect(_settings.RememberedLoginPassword);

    /// <summary>W5-5: 是否存在已记录（加密）的登录密码。</summary>
    public bool HasRememberedLoginPassword
        => !string.IsNullOrEmpty(_settings.RememberedLoginPassword);

    public string GalleryViewMode
    {
        get => _settings.GalleryViewMode;
        set => SetAndSave(value,
            () => _settings.GalleryViewMode,
            next => _settings.GalleryViewMode = next);
    }

    public string GallerySort
    {
        get => _settings.GallerySort;
        set => SetAndSave(value,
            () => _settings.GallerySort,
            next => _settings.GallerySort = next);
    }

    /// <summary>U2-B (§4.B.1): 最近入库 dropdown (null=全部, 1/7/30=N天).</summary>
    public int? GalleryRecentImportDays
    {
        get => _settings.GalleryRecentImportDays;
        set => SetAndSave(value,
            () => _settings.GalleryRecentImportDays,
            next => _settings.GalleryRecentImportDays = next);
    }

    public int GalleryGridZoom
    {
        get => _settings.GalleryGridZoom;
        set => SetAndSave(Math.Clamp(value, MIN_ZOOM, MAX_ZOOM),
            () => _settings.GalleryGridZoom,
            next => _settings.GalleryGridZoom = next);
    }

    public int GalleryListZoom
    {
        get => _settings.GalleryListZoom;
        set => SetAndSave(Math.Clamp(value, MIN_ZOOM, MAX_ZOOM),
            () => _settings.GalleryListZoom,
            next => _settings.GalleryListZoom = next);
    }

    /// <summary>B2-4-BRW browse (masonry) card width (160–512).</summary>
    public int GalleryBrowseZoom
    {
        get => _settings.GalleryBrowseZoom;
        set => SetAndSave(Math.Clamp(value, MIN_ZOOM, MAX_ZOOM),
            () => _settings.GalleryBrowseZoom,
            next => _settings.GalleryBrowseZoom = next);
    }

    /// <summary>U3-A (§4.B.2): B1-5 attribute filter section expanded.</summary>
    public bool SidebarAttributeFilterExpanded
    {
        get => _settings.SidebarAttributeFilterExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarAttributeFilterExpanded,
            next => _settings.SidebarAttributeFilterExpanded = next);
    }

    /// <summary>M12-F: B1-6 筛选标签组 section expanded.</summary>
    public bool SidebarFilterGroupsExpanded
    {
        get => _settings.SidebarFilterGroupsExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarFilterGroupsExpanded,
            next => _settings.SidebarFilterGroupsExpanded = next);
    }

    // ─── U3-C (§4.E.3): B1-COL sidebar section collapse states ─────────────

    public bool SidebarLibraryExpanded
    {
        get => _settings.SidebarLibraryExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarLibraryExpanded,
            next => _settings.SidebarLibraryExpanded = next);
    }

    public bool SidebarCollectionsExpanded
    {
        get => _settings.SidebarCollectionsExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarCollectionsExpanded,
            next => _settings.SidebarCollectionsExpanded = next);
    }

    public bool SidebarTopTagsExpanded
    {
        get => _settings.SidebarTopTagsExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarTopTagsExpanded,
            next => _settings.SidebarTopTagsExpanded = next);
    }

    public bool SidebarPbrGroupsExpanded
    {
        get => _settings.SidebarPbrGroupsExpanded;
        set => SetAndSave(value,
            () => _settings.SidebarPbrGroupsExpanded,
            next => _settings.SidebarPbrGroupsExpanded = next);
    }

    public IReadOnlyList<string> WatcherDirectories => _settings.WatcherDirectories;

    public void SetWatcherDirectories(IReadOnlyList<string> directories)
    {
        _settings.WatcherDirectories = directories.ToList();
        if (TrySaveNow())
            Changed?.Invoke();
    }

    public bool AutoImportOnWatch
    {
        get => _settings.AutoImportOnWatch;
        set => SetAndSave(value,
            () => _settings.AutoImportOnWatch,
            next => _settings.AutoImportOnWatch = next);
    }

    // ─── AI (M3) ────────────────────────────────────────────────────────────

    /// <summary>
    /// v0.12.0: AiMode 现为计算属性——由 <see cref="ActiveAiBackendId"/> 派生。
    /// getter: null→Disabled，Local backend→Local，Cloud backend→Cloud。
    /// setter: 向后兼容桥——Disabled→active=null；Local/Cloud→选首个对应 Kind
    /// 的 backend。新代码应直接读写 <see cref="ActiveAiBackendId"/>。
    /// </summary>
    public AiMode AiMode
    {
        get
        {
            int? id = _settings.ActiveAiBackendId;
            if (id is not { } bid) return AiMode.Disabled;
            AiBackendConfig? backend = _settings.AiBackends.FirstOrDefault(b => b.Id == bid);
            if (backend is null) return AiMode.Disabled;
            return backend.Kind == AiBackendKind.Local ? AiMode.Local : AiMode.Cloud;
        }
        set
        {
            int? newActiveId = value switch
            {
                AiMode.Disabled => null,
                AiMode.Local => _settings.AiBackends.FirstOrDefault(b => b.Kind == AiBackendKind.Local)?.Id,
                AiMode.Cloud => _settings.AiBackends.FirstOrDefault(b => b.Kind == AiBackendKind.Cloud)?.Id,
                _ => null,
            };
            if (_settings.ActiveAiBackendId == newActiveId) return;
            _settings.ActiveAiBackendId = newActiveId;
            if (TrySaveNow()) Changed?.Invoke();
        }
    }

    public string CloudBaseUrl
    {
        get => _settings.CloudAi.BaseUrl;
        set
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
                return; // silently reject non-HTTPS
            SetAndSave(value,
                () => _settings.CloudAi.BaseUrl,
                next => _settings.CloudAi.BaseUrl = next);
        }
    }

    public string CloudModelName
    {
        get => _settings.CloudAi.ModelName;
        set => SetAndSave(value,
            () => _settings.CloudAi.ModelName,
            next => _settings.CloudAi.ModelName = next);
    }

    public string CloudEmbeddingModelName
    {
        get => _settings.CloudAi.EmbeddingModelName;
        set => SetAndSave(value,
            () => _settings.CloudAi.EmbeddingModelName,
            next => _settings.CloudAi.EmbeddingModelName = next);
    }

    /// <summary>
    /// Encrypts the API key with DPAPI (CurrentUser) and persists it.
    /// The plaintext never appears in config.json or logs.
    /// </summary>
    public void SetCloudApiKey(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            ClearCloudApiKey();
            return;
        }
        string? encrypted = DpApiProtector.Protect(plaintext);
        if (encrypted is null)
        {
            _log("[SETTINGS] DPAPI protect failed; API key not saved.");
            return;
        }
        SetAndSave(encrypted,
            () => _settings.CloudAi.EncryptedApiKey,
            next => _settings.CloudAi.EncryptedApiKey = next);
    }

    /// <summary>
    /// Decrypts and returns the API key in plaintext.
    /// Returns null when not set or decryption fails.
    /// Callers must never log the returned value.
    /// </summary>
    public string? GetCloudApiKey()
    {
        string encrypted = _settings.CloudAi.EncryptedApiKey;
        if (string.IsNullOrEmpty(encrypted))
            return null;
        return DpApiProtector.Unprotect(encrypted);
    }

    public void ClearCloudApiKey() =>
        SetAndSave(string.Empty,
            () => _settings.CloudAi.EncryptedApiKey,
            next => _settings.CloudAi.EncryptedApiKey = next);

    public bool HasCloudApiKey =>
        !string.IsNullOrEmpty(_settings.CloudAi.EncryptedApiKey);

    /// <summary>
    /// The DPAPI-encrypted API key (Base64). For internal factory use;
    /// never log this value. Returns empty string when not set.
    /// </summary>
    public string CloudEncryptedApiKey => _settings.CloudAi.EncryptedApiKey;

    /// <summary>
    /// M3-D: source file chosen via 手动导入 (the copy that is actually used
    /// for inference lives in {DataRoot}\models\). Kept only as a convenience
    /// so the file dialog can preselect the last location.
    /// </summary>
    public string? LocalModelPath
    {
        get => _settings.LocalModelPath;
        set => SetAndSave(value,
            () => _settings.LocalModelPath,
            next => _settings.LocalModelPath = next);
    }

    // ─── AI backends (v0.12.0 multi-backend) ────────────────────────────────

    /// <summary>Read-only view of persisted AI backends; mutations go through the dedicated methods below.</summary>
    public IReadOnlyList<AiBackendConfig> AiBackends => _settings.AiBackends;

    /// <summary>Id of the active AI backend; null = disabled.</summary>
    public int? ActiveAiBackendId
    {
        get => _settings.ActiveAiBackendId;
        set
        {
            if (_settings.ActiveAiBackendId == value) return;
            _settings.ActiveAiBackendId = value;
            if (TrySaveNow()) Changed?.Invoke();
        }
    }

    /// <summary>Returns the active backend config, or null when disabled.</summary>
    public AiBackendConfig? GetActiveAiBackend()
    {
        int? id = _settings.ActiveAiBackendId;
        if (id is not { } bid) return null;
        return _settings.AiBackends.FirstOrDefault(b => b.Id == bid);
    }

    /// <summary>Finds a backend by id; null when absent.</summary>
    public AiBackendConfig? GetAiBackend(int id) =>
        _settings.AiBackends.FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// Adds a backend. Assigns a strictly monotonic id (max existing id + 1,
    /// or 1 for the first; deleted ids are never reused). Stamps
    /// <see cref="AiBackendConfig.CreatedAt"/>/<see cref="AiBackendConfig.UpdatedAt"/>.
    /// Returns the assigned id (also written back to <paramref name="backend"/>.Id).
    /// </summary>
    public int AddAiBackend(AiBackendConfig backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        int nextId = _settings.AiBackends.Count == 0
            ? 1
            : _settings.AiBackends.Max(b => b.Id) + 1;
        backend.Id = nextId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        backend.CreatedAt = now;
        backend.UpdatedAt = now;
        _settings.AiBackends.Add(backend);
        if (TrySaveNow()) Changed?.Invoke();
        return nextId;
    }

    /// <summary>
    /// Updates an existing backend by id. Copies editable fields onto the
    /// existing instance and stamps <see cref="AiBackendConfig.UpdatedAt"/>.
    /// Preserves <see cref="AiBackendConfig.EncryptedApiKey"/> and
    /// <see cref="AiBackendConfig.CreatedAt"/> — the API key is handled
    /// separately via <see cref="SetAiBackendApiKey"/>. Throws when the id
    /// is unknown. Refuses to change <see cref="AiBackendConfig.Kind"/> or
    /// <see cref="AiBackendConfig.IsBundled"/>.
    /// </summary>
    public void UpdateAiBackend(AiBackendConfig backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        AiBackendConfig? existing = _settings.AiBackends.FirstOrDefault(b => b.Id == backend.Id);
        if (existing is null)
            throw new ArgumentException($"No AI backend with id {backend.Id}.", nameof(backend));
        existing.Name = backend.Name;
        // Cloud fields
        existing.BaseUrl = backend.BaseUrl;
        existing.ModelName = backend.ModelName;
        existing.EmbeddingModelName = backend.EmbeddingModelName;
        // Local fields
        existing.ModelPath = backend.ModelPath;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        if (TrySaveNow()) Changed?.Invoke();
    }

    /// <summary>
    /// Removes the backend with the given id. Refuses to delete a bundled
    /// backend (<see cref="AiBackendConfig.IsBundled"/>=true). Clears
    /// <see cref="ActiveAiBackendId"/> if it pointed at the deleted id.
    /// No-op (not an error) when the id is absent.
    /// </summary>
    public void DeleteAiBackend(int id)
    {
        AiBackendConfig? existing = _settings.AiBackends.FirstOrDefault(b => b.Id == id);
        if (existing is null) return;
        if (existing.IsBundled)
            throw new InvalidOperationException("The bundled AI backend cannot be deleted.");
        _settings.AiBackends.RemoveAll(b => b.Id == id);
        if (_settings.ActiveAiBackendId == id)
            _settings.ActiveAiBackendId = null;
        if (TrySaveNow()) Changed?.Invoke();
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> via DPAPI (CurrentUser) and
    /// stores it on the backend with the given id. Null/empty clears the
    /// stored key. The plaintext never appears in config.json or logs.
    /// </summary>
    public void SetAiBackendApiKey(int id, string? plaintext)
    {
        AiBackendConfig? backend = _settings.AiBackends.FirstOrDefault(b => b.Id == id);
        if (backend is null) return;
        if (string.IsNullOrEmpty(plaintext))
        {
            backend.EncryptedApiKey = string.Empty;
        }
        else
        {
            string? encrypted = DpApiProtector.Protect(plaintext);
            if (encrypted is null)
            {
                _log($"[SETTINGS] DPAPI protect failed for AI backend {id}; API key not saved.");
                return;
            }
            backend.EncryptedApiKey = encrypted;
        }
        if (TrySaveNow()) Changed?.Invoke();
    }

    /// <summary>
    /// Decrypts and returns the stored API key for the backend. Returns null
    /// when not set or decryption fails. Callers must never log the result.
    /// </summary>
    public string? GetAiBackendApiKey(int id)
    {
        AiBackendConfig? backend = _settings.AiBackends.FirstOrDefault(b => b.Id == id);
        if (backend is null) return null;
        return DpApiProtector.Unprotect(backend.EncryptedApiKey);
    }

    /// <summary>True when the backend has a stored (encrypted) API key.</summary>
    public bool HasAiBackendApiKey(int id)
    {
        AiBackendConfig? backend = _settings.AiBackends.FirstOrDefault(b => b.Id == id);
        return backend is not null && !string.IsNullOrEmpty(backend.EncryptedApiKey);
    }

    // ─── PBR grouping (M4) ──────────────────────────────────────────────────

    /// <summary>
    /// Active PBR suffix rules. Null/empty in config.json resolves to the
    /// built-in defaults.
    /// </summary>
    public IReadOnlyList<PbrRule> PbrRules =>
        _settings.PbrRules is { Count: > 0 } ? _settings.PbrRules : PbrRuleSet.Defaults;

    /// <summary>
    /// Persists custom rules; null/empty restores the built-in defaults.
    /// </summary>
    public void SetPbrRules(IReadOnlyList<PbrRule>? rules)
    {
        _settings.PbrRules = rules is { Count: > 0 } ? [.. rules] : null;
        if (TrySaveNow())
            Changed?.Invoke();
    }

    // ─── NAS connections (M5) ─────────────────────────────────────────────

    /// <summary>Read-only view of persisted NAS connections; mutations go through the dedicated methods below.</summary>
    public IReadOnlyList<NasConnectionSpec> NasConnections => _settings.NasConnections;

    /// <summary>Bulk-replaces the entire NAS connection list (mirrors SetWatcherDirectories).</summary>
    public void SetNasConnections(IReadOnlyList<NasConnectionSpec> connections)
    {
        _settings.NasConnections = connections.ToList();
        if (TrySaveNow())
            Changed?.Invoke();
    }

    /// <summary>
    /// Adds a connection. Assigns a strictly monotonic id (max existing id + 1,
    /// or 1 for the first; deleted ids are never reused). Stamps CreatedAt/UpdatedAt
    /// to now. Returns the assigned id (also written back to <paramref name="spec"/>.Id).
    /// </summary>
    public int AddNasConnection(NasConnectionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int nextId = _settings.NasConnections.Count == 0
            ? 1
            : _settings.NasConnections.Max(c => c.Id) + 1;
        spec.Id = nextId;
        var now = DateTimeOffset.UtcNow;
        spec.CreatedAt = now;
        spec.UpdatedAt = now;
        _settings.NasConnections.Add(spec);
        if (TrySaveNow())
            Changed?.Invoke();
        return nextId;
    }

    /// <summary>
    /// Updates an existing connection by id. Copies the editable fields
    /// (Name/Protocol/Host/Port/RootPath/Username/UseTls/TrustSelfSigned)
    /// onto the existing instance and stamps UpdatedAt. Preserves
    /// <see cref="NasConnectionSpec.EncryptedPassword"/> and CreatedAt —
    /// password is handled separately via <see cref="SetNasPassword"/>.
    /// Throws <see cref="ArgumentException"/> when the id is unknown.
    /// </summary>
    public void UpdateNasConnection(NasConnectionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        NasConnectionSpec? existing = _settings.NasConnections.FirstOrDefault(c => c.Id == spec.Id);
        if (existing is null)
            throw new ArgumentException($"No NAS connection with id {spec.Id}.", nameof(spec));
        existing.Name = spec.Name;
        existing.Protocol = spec.Protocol;
        existing.Host = spec.Host;
        existing.Port = spec.Port;
        existing.RootPath = spec.RootPath;
        existing.Username = spec.Username;
        existing.UseTls = spec.UseTls;
        existing.TrustSelfSigned = spec.TrustSelfSigned;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        if (TrySaveNow())
            Changed?.Invoke();
    }

    /// <summary>
    /// Removes the connection with the given id. Because the password lives
    /// on the spec as EncryptedPassword, removing the spec also removes the
    /// password. No-op (not an error) when the id is absent.
    /// </summary>
    public void DeleteNasConnection(int id)
    {
        int removed = _settings.NasConnections.RemoveAll(c => c.Id == id);
        if (removed == 0) return;
        if (TrySaveNow())
            Changed?.Invoke();
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> via DPAPI (CurrentUser) and
    /// stores it on the connection with the given id. Null/empty clears the
    /// stored password. The plaintext never appears in config.json or logs.
    /// </summary>
    public void SetNasPassword(int id, string? plaintext)
    {
        NasConnectionSpec? spec = _settings.NasConnections.FirstOrDefault(c => c.Id == id);
        if (spec is null) return;
        if (string.IsNullOrEmpty(plaintext))
        {
            spec.EncryptedPassword = null;
        }
        else
        {
            string? encrypted = DpApiProtector.Protect(plaintext);
            if (encrypted is null)
            {
                _log($"[SETTINGS] DPAPI protect failed for NAS connection {id}; password not saved.");
                return;
            }
            spec.EncryptedPassword = encrypted;
        }
        if (TrySaveNow())
            Changed?.Invoke();
    }

    /// <summary>
    /// Decrypts and returns the stored password for the connection. Returns
    /// null when not set or decryption fails. Callers must never log the result.
    /// </summary>
    public string? GetNasPassword(int id)
    {
        NasConnectionSpec? spec = _settings.NasConnections.FirstOrDefault(c => c.Id == id);
        if (spec is null) return null;
        return DpApiProtector.Unprotect(spec.EncryptedPassword);
    }

    /// <summary>True when the connection has a stored (encrypted) password.</summary>
    public bool HasNasPassword(int id)
    {
        NasConnectionSpec? spec = _settings.NasConnections.FirstOrDefault(c => c.Id == id);
        return spec is not null && !string.IsNullOrEmpty(spec.EncryptedPassword);
    }

    private void SetAndSave<T>(T value, Func<T> get, Action<T> set)
    {
        T previous = get();
        if (EqualityComparer<T>.Default.Equals(previous, value)) return;
        set(value);
        if (TrySaveNow())
        {
            Changed?.Invoke();
        }
        else
        {
            set(previous);
        }
    }

    // ─── Load / Save ───────────────────────────────────────────────────────

    public void Load()
    {
        if (TryLoadFile(_settingsPath, out AppSettings? loaded, out Exception? primaryError))
        {
            _settings = loaded;
            NormalizeLoadedSettings();
            System.Diagnostics.Debug.WriteLine("[Settings] Loaded");
            return;
        }

        string backupPath = AtomicTextFile.GetBackupPath(_settingsPath);
        if (TryLoadFile(backupPath, out loaded, out Exception? backupError))
        {
            _settings = loaded;
            NormalizeLoadedSettings();
            try
            {
                AtomicTextFile.RestoreBackup(_settingsPath);
                Report($"Recovered settings from {Path.GetFileName(backupPath)}.");
            }
            catch (Exception ex)
            {
                Report("Loaded the settings recovery snapshot but could not restore the primary file.", ex);
            }
            return;
        }

        if (primaryError is not null)
            Report("Primary settings could not be loaded; defaults are active.", primaryError);
        if (backupError is not null)
            Report("Settings recovery snapshot could not be loaded.", backupError);

        _settings = new AppSettings();
        // v0.12.0: 全新默认（文件缺失/损坏）也需种子内置 backend + 校验。
        NormalizeLoadedSettings();
    }

    public bool TrySaveNow()
    {
        try
        {
            string json = JsonSerializer.Serialize(_settings, JSON_OPTIONS);
            AtomicTextFile.WriteAllText(_settingsPath, json);
            LastSaveSucceeded = true;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveSucceeded = false;
            Report("Settings save failed; the previous complete file was preserved.", ex);
            return false;
        }
    }

    public void SaveNow() => TrySaveNow();

    private void NormalizeLoadedSettings()
    {
        if (_settings.Theme is not ("Dark" or "Light"))
            _settings.Theme = "Dark";

        _settings.ThumbnailSize =
            Math.Clamp(_settings.ThumbnailSize, MIN_THUMBNAIL_SIZE, MAX_THUMBNAIL_SIZE);
        _settings.ThumbnailQuality =
            Math.Clamp(_settings.ThumbnailQuality, MIN_THUMBNAIL_QUALITY, MAX_THUMBNAIL_QUALITY);

        if (_settings.GalleryViewMode is not ("Grid" or "List" or "Browse"))
            _settings.GalleryViewMode = "Browse";

        if (string.IsNullOrWhiteSpace(_settings.GallerySort))
            _settings.GallerySort = "ImportedDesc";

        _settings.GalleryGridZoom =
            Math.Clamp(_settings.GalleryGridZoom, MIN_ZOOM, MAX_ZOOM);
        _settings.GalleryListZoom =
            Math.Clamp(_settings.GalleryListZoom, MIN_ZOOM, MAX_ZOOM);
        _settings.GalleryBrowseZoom =
            Math.Clamp(_settings.GalleryBrowseZoom, MIN_ZOOM, MAX_ZOOM);

        _settings.WatcherDirectories ??= [];

        // AI fields
        if (!Enum.IsDefined(_settings.AiMode))
            _settings.AiMode = AiMode.Disabled;
        _settings.CloudAi ??= new CloudAiConfig();
        if (!Uri.TryCreate(_settings.CloudAi.BaseUrl, UriKind.Absolute, out Uri? u) ||
            u!.Scheme != Uri.UriSchemeHttps)
            _settings.CloudAi.BaseUrl = string.Empty;

        // v0.12.0: 多后端迁移 + 内置种子 + 活跃 Id 校验
        NormalizeAiBackends();

        // M11: 空白库目录视为未配置；已配置的规范化为绝对路径（失败保留原值，
        // 由向导/库服务在真正访问时暴露问题）。
        if (string.IsNullOrWhiteSpace(_settings.LibraryRootPath))
        {
            _settings.LibraryRootPath = null;
        }
        else
        {
            try { _settings.LibraryRootPath = Path.GetFullPath(_settings.LibraryRootPath.Trim()); }
            catch (Exception ex)
            {
                Report($"Configured LibraryRootPath is not a valid path; keeping raw value.", ex);
            }
        }

        // DataRootPath 同样规范化；空白归一化为 null。
        if (string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            _settings.DataRootPath = null;
        }
        else
        {
            try { _settings.DataRootPath = Path.GetFullPath(_settings.DataRootPath.Trim()); }
            catch (Exception ex)
            {
                Report($"Configured DataRootPath is not a valid path; keeping raw value.", ex);
            }
        }

        // NAS fields
        _settings.NasConnections ??= [];
        _settings.ThumbnailCacheLimitMb = Math.Clamp(
            _settings.ThumbnailCacheLimitMb, MIN_THUMBNAIL_CACHE_MB, MAX_THUMBNAIL_CACHE_MB);
        // Defensive: ensure every spec has sane CreatedAt/UpdatedAt (older saves
        // may have default DateTimeOffset values from before the fields existed).
        foreach (var c in _settings.NasConnections)
        {
            if (c.CreatedAt == default) c.CreatedAt = DateTimeOffset.UtcNow;
            if (c.UpdatedAt == default) c.UpdatedAt = c.CreatedAt;
        }
        // M8-A: 校验 NasRole——≤1 个 Primary；无 Primary 时第一项收敛为 Primary；
        // ShareLink 角色强制无账号密码（System.Text.Json 不识别 [OnDeserialized]，
        // 这里集中处理）。
        ValidateNasRoles();
    }

    /// <summary>
    /// M8-A: 强约束：全列表至多 1 个 Primary；无 Primary 时第一项设为 Primary
    /// （沿用 v0.2.1 兜底）；多 Primary 时只保留第一个为 Primary，其他降级 Secondary；
    /// ShareLink 强制清空 Username/EncryptedPassword。空列表跳过。
    /// </summary>
    private void ValidateNasRoles()
    {
        if (_settings.NasConnections.Count == 0) return;

        bool seenPrimary = false;
        NasConnectionSpec? firstPrimary = null;
        foreach (var c in _settings.NasConnections)
        {
            // ShareLink 强制无凭据（防用户错填或旧数据迁移）
            if (c.Role == NasRole.ShareLink)
            {
                c.Username = string.Empty;
                c.EncryptedPassword = null;
            }

            if (c.Role == NasRole.Primary)
            {
                if (seenPrimary)
                {
                    // 已有 Primary，多余的降级
                    c.Role = NasRole.Secondary;
                }
                else
                {
                    seenPrimary = true;
                    firstPrimary = c;
                }
            }
        }

        if (!seenPrimary)
        {
            // 无 Primary：第一项设为 Primary（与 v0.2.1 行为一致）
            _settings.NasConnections[0].Role = NasRole.Primary;
        }
    }

    /// <summary>
    /// v0.12.0: 多后端 AI 迁移 + 内置种子 + 活跃 Id 校验。
    /// <para>首次加载（AiBackends 为空）：</para>
    /// <list type="bullet">
    /// <item>种子内置 Local backend（Id=1, IsBundled=true）。</item>
    /// <item>若旧 CloudAi 有实质内容 → 追加为 Cloud backend（Id=2）。</item>
    /// <item>新装（!_settingsExistedAtStartup）→ ActiveAiBackendId=1（Local 即用）。</item>
    /// <item>旧装升级 → 按旧 AiMode 决定活跃 backend（Cloud→云端, Local→内置, Disabled→null）。</item>
    /// </list>
    /// <para>已迁移（AiBackends 非空）：校验 ActiveAiBackendId 指向存在的 backend，孤儿→null。</para>
    /// </summary>
    private void NormalizeAiBackends()
    {
        _settings.AiBackends ??= [];

        if (_settings.AiBackends.Count == 0)
        {
            // 内置 Local backend（Id=1，不可删除）
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var bundled = new AiBackendConfig
            {
                Id = 1,
                Name = "内置模型",
                Kind = AiBackendKind.Local,
                ModelPath = null,
                IsBundled = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _settings.AiBackends.Add(bundled);

            // 旧 CloudAi 有实质内容 → 迁移为 Cloud backend（Id=2）
            int? migratedCloudId = null;
            bool hasCloudConfig = !string.IsNullOrWhiteSpace(_settings.CloudAi.BaseUrl)
                || !string.IsNullOrWhiteSpace(_settings.CloudAi.ModelName)
                || !string.IsNullOrEmpty(_settings.CloudAi.EncryptedApiKey);
            if (hasCloudConfig)
            {
                var cloud = new AiBackendConfig
                {
                    Id = 2,
                    Name = string.IsNullOrWhiteSpace(_settings.CloudAi.ModelName)
                        ? "云端 AI" : _settings.CloudAi.ModelName,
                    Kind = AiBackendKind.Cloud,
                    BaseUrl = _settings.CloudAi.BaseUrl,
                    ModelName = _settings.CloudAi.ModelName,
                    EncryptedApiKey = _settings.CloudAi.EncryptedApiKey,
                    EmbeddingModelName = _settings.CloudAi.EmbeddingModelName,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _settings.AiBackends.Add(cloud);
                migratedCloudId = 2;
            }

            // 决定活跃 backend
            if (!_settingsExistedAtStartup)
            {
                // 新装：默认内置 Local（即用）
                _settings.ActiveAiBackendId = 1;
            }
            else
            {
                // 旧装升级：按旧 AiMode
                _settings.ActiveAiBackendId = _settings.AiMode switch
                {
                    AiMode.Cloud => migratedCloudId,
                    AiMode.Local => 1,
                    _ => null, // Disabled
                };
            }
        }
        else
        {
            // 已迁移：校验活跃 Id，孤儿→null
            if (_settings.ActiveAiBackendId is { } aid &&
                _settings.AiBackends.All(b => b.Id != aid))
            {
                _settings.ActiveAiBackendId = null;
            }
            // 防御性：补全缺失时间戳（旧存档可能无）
            foreach (AiBackendConfig b in _settings.AiBackends)
            {
                if (b.CreatedAt == default) b.CreatedAt = DateTimeOffset.UtcNow;
                if (b.UpdatedAt == default) b.UpdatedAt = b.CreatedAt;
            }
        }
    }

    private static bool TryLoadFile(
        string path,
        out AppSettings settings,
        out Exception? error)
    {
        settings = new AppSettings();
        error = null;
        if (!File.Exists(path)) return false;

        try
        {
            string json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JSON_OPTIONS)
                ?? throw new InvalidDataException("Settings JSON contained null.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private void Report(string message, Exception? exception = null)
    {
        string detail = exception is null
            ? message
            : $"{message} {exception.GetType().Name}: {exception.Message}";
        System.Diagnostics.Debug.WriteLine($"[Settings] {detail}");
        _log($"[SETTINGS] {detail}");
    }
}
