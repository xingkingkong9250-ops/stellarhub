namespace QL.World.Models;

/// <summary>Provenance of an image tag. Manual tags are never overwritten by AI.</summary>
public enum TagSource
{
    Manual = 0,
    Ai = 1,
}

/// <summary>W1/B2-2 gallery sort modes.</summary>
public enum GallerySort
{
    ImportedDesc,
    ImportedAsc,
    NameAsc,
    RatingDesc,
    SizeDesc,
}

/// <summary>W1/A1-5 search modes. Semantic arrives in M3.</summary>
public enum SearchMode
{
    /// <summary>Match one of the image's tags.</summary>
    Tag,

    /// <summary>Substring match over file name and notes.</summary>
    FileNameNotes,
}

/// <summary>
/// The ten fixed first-level categories (B1-1).
/// <see cref="Uncategorized"/> (null in the database) is shown as 未分类.
/// </summary>
public static class Categories
{
    public const string Texture = "贴图/纹理";
    public const string PhotoReference = "实拍参考";
    public const string ConceptDesign = "概念设计";
    public const string SculptReference = "雕刻参考";
    public const string MaterialReference = "材质参考";
    public const string EnvironmentScene = "环境/场景";
    public const string LightingReference = "灯光参考";
    public const string ColorReference = "配色参考";
    public const string UiHud = "UI/HUD";
    public const string AnimationReference = "动画参考";

    /// <summary>Display order for B1-1; database value may be null (未分类).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Texture, PhotoReference, ConceptDesign, SculptReference, MaterialReference,
        EnvironmentScene, LightingReference, ColorReference, UiHud, AnimationReference,
    ];

    public static bool IsKnown(string? category) =>
        category is not null && All.Contains(category);
}
