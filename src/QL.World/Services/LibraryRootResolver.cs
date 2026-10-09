using System.IO;

namespace QL.World.Services;

/// <summary>
/// M11: resolves every on-disk path inside the software-managed image
/// library and validates the same-volume precondition for NTFS hardlinks.
///
/// Layout:
///   {root}\files\{h0}\{h0h1h2h3}\{stem}_{entryCode}{ext}   physical CAS file
///   {root}\{category}\{stem}_{entryCode}{ext}              category hardlink
///
/// The hash sharding spreads up to 16 top-level / 65 536 second-level
/// directories so a single folder never holds millions of entries.
/// This type is a pure path helper — it creates directories on demand
/// (via <see cref="EnsureLibraryLayout"/>) but never copies or links
/// bytes; that is <see cref="LibraryStorageService"/>'s job (M11-C).
/// </summary>
public static class LibraryRootResolver
{
    /// <summary>CAS physical layer folder under the library root.</summary>
    public const string FilesFolderName = "files";

    /// <summary>Category used when an import carries no primary category.</summary>
    public const string UncategorizedCategoryName = "未分类";

    /// <summary>
    /// Creates the library root and the physical files root. Category
    /// directories are created lazily per import. Idempotent.
    /// </summary>
    public static void EnsureLibraryLayout(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(Path.Combine(libraryRoot, FilesFolderName));
    }

    /// <summary>
    /// NTFS hardlinks require both paths on the same volume. The wizard
    /// calls this before accepting the chosen folder. Returns false with a
    /// user-facing message instead of throwing so the dialog can stay open.
    /// </summary>
    /// <param name="libraryRoot">User-chosen library directory.</param>
    /// <param name="referenceRoot">Data root (DB/config live here); hardlinks and physical files share the DB volume.</param>
    public static (bool Ok, string? Error) ValidateSameVolume(string libraryRoot, string referenceRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return (false, "请选择图片库目录。");
        if (string.IsNullOrWhiteSpace(referenceRoot))
            return (false, "数据目录无效，无法进行同盘校验。");

        string libraryFull;
        string referenceFull;
        try
        {
            libraryFull = Path.GetFullPath(libraryRoot);
            referenceFull = Path.GetFullPath(referenceRoot);
        }
        catch (Exception ex)
        {
            return (false, $"路径无效：{ex.Message}");
        }

        string? libraryVolume = Path.GetPathRoot(libraryFull);
        string? referenceVolume = Path.GetPathRoot(referenceFull);
        if (string.IsNullOrEmpty(libraryVolume) || string.IsNullOrEmpty(referenceVolume))
            return (false, "无法识别路径所在盘符。");

        if (!string.Equals(libraryVolume, referenceVolume, StringComparison.OrdinalIgnoreCase))
        {
            return (false,
                $"图片库目录必须与数据目录位于同一盘符（NTFS 硬链接限制）。\n" +
                $"数据目录盘符：{referenceVolume}\n当前选择盘符：{libraryVolume}");
        }

        return (true, null);
    }

    /// <summary>
    /// Physical CAS path for a content hash:
    /// files\{h0}\{h0h1h2h3}\{stem}_{entryCode}{ext}.
    /// </summary>
    public static string GetPhysicalPath(
        string libraryRoot, string contentHash, string originalFileName, string entryCode)
    {
        ValidateHash(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryCode);
        string shardDir = GetPhysicalShardDirectory(libraryRoot, contentHash);
        return Path.Combine(shardDir, BuildStoredFileName(originalFileName, entryCode));
    }

    /// <summary>files\{h0}\{h0h1h2h3}\ (created lazily by the storage service).</summary>
    public static string GetPhysicalShardDirectory(string libraryRoot, string contentHash)
    {
        ValidateHash(contentHash);
        return Path.Combine(
            libraryRoot,
            FilesFolderName,
            contentHash[0..1],
            contentHash[0..4]);
    }

    /// <summary>
    /// Category hardlink path: {root}\{safeCategory}\{stem}_{entryCode}{ext}.
    /// The category segment is sanitized so a category can never escape the
    /// library root via separators or "..".
    /// </summary>
    public static string GetCategoryHardlinkPath(
        string libraryRoot, string? category, string originalFileName, string entryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryCode);
        string safeCategory = SanitizeCategoryName(category);
        return Path.Combine(
            libraryRoot,
            safeCategory,
            BuildStoredFileName(originalFileName, entryCode));
    }

    /// <summary>{root}\{safeCategory}\ — created lazily per import.</summary>
    public static string GetCategoryDirectory(string libraryRoot, string? category)
        => Path.Combine(libraryRoot, SanitizeCategoryName(category));

    /// <summary>
    /// Maps a raw primary category to a single safe folder-segment name.
    /// Null/empty/whitespace → <see cref="UncategorizedCategoryName"/>;
    /// path separators and invalid chars are replaced with '_'; a result
    /// consisting only of dots (e.g. "..") also falls back to 未分类.
    /// </summary>
    public static string SanitizeCategoryName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return UncategorizedCategoryName;

        char[] invalid =
        [
            .. Path.GetInvalidFileNameChars(),
            .. Path.GetInvalidPathChars(),
            '\\', '/', ':', '*', '?', '"', '<', '>', '|',
        ];
        var cleaned = new string(category.Trim()
            .Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)
            .ToArray());

        // Guard against "." / ".." / "  " style segments after sanitization.
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.All(ch => ch == '.' || ch == ' '))
            return UncategorizedCategoryName;

        // Folder names ending in space/dot are awkward on Windows; trim them.
        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? UncategorizedCategoryName : cleaned;
    }

    /// <summary>
    /// Builds the on-disk file name: original stem + "_" + entry code +
    /// original extension (lowercased). Guards against an empty stem
    /// (e.g. ".png") and strips invalid file-name characters from the stem.
    /// </summary>
    public static string BuildStoredFileName(string originalFileName, string entryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryCode);
        string name = string.IsNullOrWhiteSpace(originalFileName) ? "image" : originalFileName.Trim();
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);

        char[] invalid = [.. Path.GetInvalidFileNameChars()];
        stem = new string(stem
            .Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)
            .ToArray());
        if (string.IsNullOrWhiteSpace(stem))
            stem = "image";

        return $"{stem}_{entryCode}{ext.ToLowerInvariant()}";
    }

    private static void ValidateHash(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length < 4)
            throw new ArgumentException(
                "Content hash must be at least 4 characters for sharding.", nameof(contentHash));
    }
}
