namespace QL.World.Models;

/// <summary>
/// Combined gallery filter. B1 tree selections and A1 search combine with AND.
/// Null/empty members are inactive.
/// </summary>
public sealed class LibraryFilter
{
    /// <summary>Exact primary category; null = no category filter.</summary>
    public string? Category { get; set; }

    /// <summary>When true, category filter means "未分类" (primary_category IS NULL).</summary>
    public bool Uncategorized { get; set; }

    /// <summary>Single tag filter from B1-3 / B1-1 chips.</summary>
    public string? Tag { get; set; }

    /// <summary>Collection filter arrives in M2.</summary>
    public long? CollectionId { get; set; }

    /// <summary>PBR texture-group filter from B1-4; null = no group filter.</summary>
    public long? PbrGroupId { get; set; }

    /// <summary>Free text interpreted per <see cref="SearchMode"/>.</summary>
    public string? Text { get; set; }

    public SearchMode SearchMode { get; set; } = SearchMode.Tag;

    public GallerySort Sort { get; set; } = GallerySort.ImportedDesc;

    /// <summary>
    /// U2-B (§4.B.1): restrict to images imported within the last N days.
    /// Null = no time filter (show all). 1 = today, 7 = last 7 days, etc.
    /// Compared against images.created_at (UTC ISO-8601).
    /// </summary>
    public int? ImportedWithinDays { get; set; }

    public bool OnlyMissing { get; set; }

    // ─── U3-A (§4.B.2): B1-5 attribute filters; null = 全部 ───────────────

    /// <summary>Resolution bucket key on the long edge (see <see cref="ResolutionBuckets"/>).</summary>
    public string? ResolutionBucket { get; set; }

    /// <summary>File size bucket key (see <see cref="FileSizeBuckets"/>).</summary>
    public string? FileSizeBucket { get; set; }

    /// <summary>Rating bucket key (see <see cref="RatingBuckets"/>).</summary>
    public string? RatingBucket { get; set; }

    /// <summary>
    /// Pre-computed top-N image ids from semantic search (text→image or image→image).
    /// When non-empty, the gallery restricts results to these ids (ignoring Text/Tag/Category filters).
    /// Null = no semantic result active.
    /// </summary>
    public IReadOnlyList<long>? SemanticResultIds { get; set; }

    // ─── M12-E: 标签组筛选（组内 OR，组间 AND）；null/空 = 不筛选 ──────

    /// <summary>美术风格标签（多选）。</summary>
    public IReadOnlyList<string>? ArtStyleTags { get; set; }

    /// <summary>画面构图标签（多选）。</summary>
    public IReadOnlyList<string>? CompositionTags { get; set; }

    /// <summary>灯光氛围标签（多选）。</summary>
    public IReadOnlyList<string>? LightingTags { get; set; }

    /// <summary>题材气质标签（多选）。</summary>
    public IReadOnlyList<string>? ThemeTags { get; set; }

    /// <summary>状态形容标签（多选）。</summary>
    public IReadOnlyList<string>? ConditionTags { get; set; }

    /// <summary>文件格式（多选，如 "png"/"jpg"；与 i.format 不区分大小写比对）。</summary>
    public IReadOnlyList<string>? FileFormats { get; set; }

    /// <summary>
    /// 色彩倾向色板选取的 "#RRGGBB" 列表。逐个经 ColorFamily.HexToFamily
    /// 映射为色族后与 i.color_family 匹配（OR）；无法解析的 hex 被忽略。
    /// </summary>
    public IReadOnlyList<string>? ColorPaletteHexes { get; set; }

    /// <summary>Reference image id for image→image similar search (UI display only; filtering uses SemanticResultIds).</summary>
    public long? SimilarToImageId { get; set; }

    /// <summary>
    /// Server-side pagination (M4-D): max rows returned; null = unlimited.
    /// <see cref="Offset"/> is ignored unless Limit is set.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>Row offset for pagination; only applied together with Limit.</summary>
    public int Offset { get; set; }

    public LibraryFilter Clone() => new()
    {
        Category = Category,
        Uncategorized = Uncategorized,
        Tag = Tag,
        CollectionId = CollectionId,
        PbrGroupId = PbrGroupId,
        Text = Text,
        SearchMode = SearchMode,
        Sort = Sort,
        ImportedWithinDays = ImportedWithinDays,
        OnlyMissing = OnlyMissing,
        ResolutionBucket = ResolutionBucket,
        FileSizeBucket = FileSizeBucket,
        RatingBucket = RatingBucket,
        SemanticResultIds = SemanticResultIds,
        ArtStyleTags = ArtStyleTags,
        CompositionTags = CompositionTags,
        LightingTags = LightingTags,
        ThemeTags = ThemeTags,
        ConditionTags = ConditionTags,
        FileFormats = FileFormats,
        ColorPaletteHexes = ColorPaletteHexes,
        SimilarToImageId = SimilarToImageId,
        Limit = Limit,
        Offset = Offset,
    };
}

/// <summary>Category/tag counts for B1.</summary>
public sealed record CategoryCount(string? Category, int Count);

public sealed record TagCount(string Tag, int Count);

/// <summary>U3-A: resolution bucket keys on the long edge MAX(width,height).</summary>
public static class ResolutionBuckets
{
    public const string Lt1024 = "lt1024";
    public const string From1024To2047 = "1024-2047";
    public const string From2048To4095 = "2048-4095";
    public const string Ge4096 = "ge4096";
}

/// <summary>U3-A: file size bucket keys (file_size in bytes).</summary>
public static class FileSizeBuckets
{
    public const string Lt1Mb = "lt1mb";
    public const string From1To5Mb = "1-5mb";
    public const string From5To20Mb = "5-20mb";
    public const string Ge20Mb = "ge20mb";
}

/// <summary>U3-A: rating bucket keys.</summary>
public static class RatingBuckets
{
    public const string Unrated = "unrated";
    public const string Ge3 = "ge3";
    public const string Ge4 = "ge4";
    public const string Eq5 = "eq5";
}
