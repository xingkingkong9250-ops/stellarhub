namespace QL.World.Models;

/// <summary>A saved collection (favorites group) row.</summary>
public sealed record Collection(
    long Id,
    string Name,
    string Description,
    long? CoverImageId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Collection list item with image count (for B1-2 tree).</summary>
public sealed record CollectionCount(
    long Id,
    string Name,
    int ImageCount,
    long? CoverImageId,
    string? CoverHash);

/// <summary>A single collection-image association row.</summary>
public sealed record CollectionImage(
    long CollectionId,
    long ImageId,
    int SortOrder);
