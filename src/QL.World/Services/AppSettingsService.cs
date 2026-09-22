using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QL.World.Interop;
using QL.World.Models;
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

        if (_settings.GalleryViewMode is not ("Grid" or "List"))
            _settings.GalleryViewMode = "Grid";

        if (string.IsNullOrWhiteSpace(_settings.GallerySort))
            _settings.GallerySort = "ImportedDesc";

        _settings.GalleryGridZoom =
            Math.Clamp(_settings.GalleryGridZoom, MIN_ZOOM, MAX_ZOOM);
        _settings.GalleryListZoom =
            Math.Clamp(_settings.GalleryListZoom, MIN_ZOOM, MAX_ZOOM);

        _settings.WatcherDirectories ??= [];

        // AI fields
        if (!Enum.IsDefined(_settings.AiMode))
            _settings.AiMode = AiMode.Disabled;
        _settings.CloudAi ??= new CloudAiConfig();
        if (!Uri.TryCreate(_settings.CloudAi.BaseUrl, UriKind.Absolute, out Uri? u) ||
            u!.Scheme != Uri.UriSchemeHttps)
            _settings.CloudAi.BaseUrl = string.Empty;
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
