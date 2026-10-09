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
    }

    /// <summary>The QL World data root this settings file lives in (e.g. for the models directory).</summary>
    public string DataRoot => _dataRoot;

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
    /// M11: 由 W7 向导调用，持久化用户选择的库目录（规范化为绝对路径）。
    /// 同卷校验在向导层通过 <see cref="LibraryRootResolver.ValidateSameVolume"/>
    /// 完成；这里只做空值防御与路径规范化。M11 不支持改库，故无公开 setter。
    /// </summary>
    public void ConfigureLibraryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path.Trim());
        SetAndSave(full,
            () => _settings.LibraryRootPath,
            next => _settings.LibraryRootPath = next);
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

    public AiMode AiMode
    {
        get => _settings.AiMode;
        set => SetAndSave(value,
            () => _settings.AiMode,
            next => _settings.AiMode = next);
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
            _settings.GalleryViewMode = "Grid";

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
