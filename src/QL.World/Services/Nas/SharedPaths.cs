namespace QL.World.Services.Nas;

/// <summary>
/// U6-A: forward-slash paths inside the NAS shared directory. Single source
/// for the StellarHubShared layout used by SharedStorageService, the shared
/// thumbnail cache, submission upload, and review approval (file relocation).
/// </summary>
internal static class SharedPaths
{
    public const string Root = "StellarHubShared";
    public const string AccountsFile = "accounts.json";
    public const string LibraryFile = "library.json";
    public const string ThumbnailsDir = "thumbnails";
    public const string OriginalsDir = "originals";
    public const string PendingDir = "pending";
    public const string PendingFilesDir = "files";

    public static string Accounts => $"{Root}/{AccountsFile}";
    public static string Library => $"{Root}/{LibraryFile}";

    /// <summary>Library thumbnail: thumbnails/&lt;hh&gt;/&lt;hash&gt;_thumb.webp.</summary>
    public static string Thumbnail(string hash) =>
        $"{Root}/{ThumbnailsDir}/{Prefix(hash)}/{hash}_thumb.webp";

    /// <summary>Library original: originals/&lt;hh&gt;/&lt;hash&gt;&lt;ext&gt;.</summary>
    public static string Original(string hash, string extension) =>
        $"{Root}/{OriginalsDir}/{Prefix(hash)}/{hash}{NormalizeExt(extension)}";

    public static string PendingJson(string submitId) =>
        $"{Root}/{PendingDir}/{submitId}.json";

    public static string PendingFiles(string submitId) =>
        $"{Root}/{PendingDir}/{PendingFilesDir}/{submitId}";

    public static string PendingOriginal(string submitId, string fileName) =>
        $"{PendingFiles(submitId)}/{fileName}";

    public static string PendingThumbnail(string submitId) =>
        $"{PendingFiles(submitId)}/thumb.webp";

    /// <summary>First two hex chars of the hash as a sharding prefix.</summary>
    public static string Prefix(string hash) =>
        hash.Length >= 2 ? hash[..2] : "00";

    private static string NormalizeExt(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return string.Empty;
        return extension.StartsWith('.') ? extension : $".{extension}";
    }
}
