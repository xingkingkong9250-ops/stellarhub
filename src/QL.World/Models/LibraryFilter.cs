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

    public bool OnlyMissing { get; set; }

    /// <summary>
    /// Pre-computed top-N image ids from semantic search (text→image or image→image).
    /// When non-empty, the gallery restricts results to these ids (ignoring Text/Tag/Category filters).
    /// Null = no semantic result active.
    /// </summary>
    public IReadOnlyList<long>? SemanticResultIds { get; set; }

    /// <summary>Reference image id for image→image similar search (UI display only; filtering uses SemanticResultIds).</summary>
    public long? SimilarToImageId { get; set; }

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
        OnlyMissing = OnlyMissing,
        SemanticResultIds = SemanticResultIds,
        SimilarToImageId = SimilarToImageId,
    };
}

/// <summary>Category/tag counts for B1.</summary>
public sealed record CategoryCount(string? Category, int Count);

public sealed record TagCount(string Tag, int Count);
