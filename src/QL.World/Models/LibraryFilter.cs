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

    /// <summary>Free text interpreted per <see cref="SearchMode"/>.</summary>
    public string? Text { get; set; }

    public SearchMode SearchMode { get; set; } = SearchMode.Tag;

    public GallerySort Sort { get; set; } = GallerySort.ImportedDesc;

    public bool OnlyMissing { get; set; }

    public LibraryFilter Clone() => new()
    {
        Category = Category,
        Uncategorized = Uncategorized,
        Tag = Tag,
        CollectionId = CollectionId,
        Text = Text,
        SearchMode = SearchMode,
        Sort = Sort,
        OnlyMissing = OnlyMissing,
    };
}

/// <summary>Category/tag counts for B1.</summary>
public sealed record CategoryCount(string? Category, int Count);

public sealed record TagCount(string Tag, int Count);
