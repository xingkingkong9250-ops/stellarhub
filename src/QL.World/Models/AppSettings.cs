using QL.World.Services.Nas;

namespace QL.World.Models;

/// <summary>
/// Persisted UI/preference settings (config.json).
/// M0 only carries shell/preview options; AI and watcher settings
/// arrive with their milestones (M2/M3).
/// </summary>
public sealed class AppSettings
{
    /// <summary>"Dark" (default) or "Light". Light theme lands in M2.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Verify library integrity on startup (W3/A1).</summary>
    // 默认 true 的 bool 必须 Never 忽略：WhenWritingDefault 会吞掉 false，导致重载回落 true。
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool CheckIntegrityOnStartup { get; set; } = true;

    /// <summary>Generated thumbnail edge length in pixels (128–512).</summary>
    public int ThumbnailSize { get; set; } = 200;

    /// <summary>WebP encoder quality (30–100).</summary>
    public int ThumbnailQuality { get; set; } = 82;

    /// <summary>W1/B2 gallery presentation: "Grid" / "List" / "Browse".</summary>
    public string GalleryViewMode { get; set; } = "Grid";

    /// <summary>W1/B2-2 last chosen sort key.</summary>
    public string GallerySort { get; set; } = "ImportedDesc";

    /// <summary>U2-B (§4.B.1): 最近入库 dropdown selection (null=全部, 1=今天, 7/30=N天).</summary>
    public int? GalleryRecentImportDays { get; set; }

    /// <summary>B2-4 grid zoom (80–512).</summary>
    public int GalleryGridZoom { get; set; } = 200;

    /// <summary>B2-4 list row thumbnail zoom (80–512).</summary>
    public int GalleryListZoom { get; set; } = 96;

    /// <summary>B2-4-BRW browse (masonry) card width (160–512).</summary>
    public int GalleryBrowseZoom { get; set; } = 240;

    /// <summary>U3-A (§4.B.2): B1-5 attribute filter section expanded (default collapsed).</summary>
    public bool SidebarAttributeFilterExpanded { get; set; }

    // ─── U3-C (§4.E.3): B1-COL sidebar section collapse states (default all expanded) ──
    // 默认 true 的 bool 必须 Never 忽略：WhenWritingDefault 会吞掉 false，导致重载回落 true。

    /// <summary>B1-1 素材库 section expanded.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool SidebarLibraryExpanded { get; set; } = true;

    /// <summary>B1-2 收藏夹 section expanded.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool SidebarCollectionsExpanded { get; set; } = true;

    /// <summary>B1-3 热门标签 Top50 section expanded.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool SidebarTopTagsExpanded { get; set; } = true;

    /// <summary>B1-4 贴图组 section expanded.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool SidebarPbrGroupsExpanded { get; set; } = true;

    /// <summary>Directories monitored by FileWatcherService (W3/A4).</summary>
    public List<string> WatcherDirectories { get; set; } = [];

    /// <summary>Auto-import new files found by the watcher.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool AutoImportOnWatch { get; set; } = true;

    // ─── AI (M3) ────────────────────────────────────────────────────────────

    /// <summary>AI analysis mode. Defaults to Disabled; AI is a pure add-on.</summary>
    public AiMode AiMode { get; set; } = AiMode.Disabled;

    /// <summary>
    /// v0.10.0: AI 自适应学习开关（词表学习 + 保留率重排 + 参考样本 kNN，
    /// 仅本地后端；同时留存训练数据快照）。默认开启。
    /// </summary>
    public bool AiLearningEnabled { get; set; } = true;

    /// <summary>Cloud AI service configuration (base URL, model, encrypted key).</summary>
    public CloudAiConfig CloudAi { get; set; } = new();

    /// <summary>Path to the local ONNX model file (M3-D). Null when not downloaded.</summary>
    public string? LocalModelPath { get; set; }

    // ─── PBR grouping (M4) ──────────────────────────────────────────────────

    /// <summary>
    /// Editable PBR suffix rules. Null/empty means the built-in defaults
    /// (PbrRuleSet.Defaults) are active.
    /// </summary>
    public List<PbrRule>? PbrRules { get; set; }

    // ─── NAS (M5) ──────────────────────────────────────────────────────────

    /// <summary>M5-C: persisted NAS connection definitions. Empty for new installs.</summary>
    public List<NasConnectionSpec> NasConnections { get; set; } = [];

    /// <summary>
    /// M5-C: local thumbnail cache size cap in MB (used by M5-F LRU cleanup).
    /// Default 1024. Clamped to [256, 16384] on load.
    /// </summary>
    public int ThumbnailCacheLimitMb { get; set; } = 1024;

    /// <summary>
    /// M8-E: 是否在共享画廊显示数据来源徽章（nas://{id}）。默认 false。
    /// 管理员可在 A7 设置开关；开启后每张共享图带 nas:// 来源标识。
    /// </summary>
    public bool ShowSourceBadge { get; set; } = false;

    // ─── M11: 软件托管图片库 ───────────────────────────────────────────────

    /// <summary>
    /// M11: 软件托管图片库根目录（W7 首次启动向导选择）。
    /// 物理文件落 {root}\files\，分类硬链接落 {root}\<一级分类>\。
    /// null = 尚未配置（W7 未完成）。M11 不支持修改，W3 仅只读显示。
    /// </summary>
    public string? LibraryRootPath { get; set; }

    /// <summary>
    /// M11: 老用户在 W7 向导选择「跳过」迁移后置 true。软跳过语义：
    /// 不清空 images 表，画廊默认仅显示 library_hash 非空且已托管的行，
    /// 由 <see cref="ShowLegacyImages"/> 开关控制是否可见。可逆。
    /// </summary>
    public bool LegacyDataSkipped { get; set; }

    /// <summary>
    /// M11: W3/A1「显示未托管图」开关。默认 false——跳过老数据后画廊
    /// 不显示未托管行；开启后画廊 SQL 放行 library_hash 过滤。
    /// </summary>
    public bool ShowLegacyImages { get; set; }

    // ─── Login (W5) ────────────────────────────────────────────────────────

    /// <summary>
    /// W5-4: 勾选「记住账户」时持久化的登录用户名；未记住为 null。
    /// </summary>
    public string? RememberedLoginUsername { get; set; }

    /// <summary>
    /// W5-5: 勾选「记录密码」时持久化的登录密码（DPAPI CurrentUser 加密的
    /// Base64）；未记录为 null。明文永不落盘。
    /// </summary>
    public string? RememberedLoginPassword { get; set; }
}
