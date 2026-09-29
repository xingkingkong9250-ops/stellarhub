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

    /// <summary>W1/B2 gallery presentation: "Grid" or "List".</summary>
    public string GalleryViewMode { get; set; } = "Grid";

    /// <summary>W1/B2-2 last chosen sort key.</summary>
    public string GallerySort { get; set; } = "ImportedDesc";

    /// <summary>U2-B (§4.B.1): 最近入库 dropdown selection (null=全部, 1=今天, 7/30=N天).</summary>
    public int? GalleryRecentImportDays { get; set; }

    /// <summary>B2-4 grid zoom (80–512).</summary>
    public int GalleryGridZoom { get; set; } = 200;

    /// <summary>B2-4 list row thumbnail zoom (80–512).</summary>
    public int GalleryListZoom { get; set; } = 96;

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
}
