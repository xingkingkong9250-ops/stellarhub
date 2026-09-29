using System.IO;

namespace QL.World.Services;

internal static class AppDataRootResolver
{
    internal const string OverrideEnvironmentVariable = "QL_WORLD_DATA_ROOT";

    internal const string CompanyFolder = "Qianlan_App";
    internal const string ProductFolder = "StellarHub";      // U5-C: was "QL_World"
    internal const string LegacyProductFolder = "QL_World";  // U5-C: migration source fallback

    internal const string DatabaseFileName = "qlworld.db";
    internal const string ConfigFileName = "config.json";
    internal const string ThumbnailsFolderName = "thumbnails";
    internal const string ModelsFolderName = "models";
    internal const string LogsFolderName = "logs";

    /// <summary>Marker file under the new product folder once migration succeeded (idempotency).</summary>
    internal const string MigrationMarkerFileName = ".migrated";
    /// <summary>Diagnostic log written into the legacy folder when migration failed.</summary>
    internal const string MigrationFailedMarkerFileName = ".migration-failed";

    /// <summary>
    /// Top-level entries inside the legacy product folder that participate in
    /// the one-shot move to the new path. SQLite sidecar files (-wal/-shm)
    /// and `.backup` suffixes are covered; unknown stray files are left behind
    /// (the user can copy them manually).
    /// </summary>
    private static readonly string[] MigratableEntries =
    {
        DatabaseFileName,
        DatabaseFileName + "-wal",
        DatabaseFileName + "-shm",
        DatabaseFileName + ".backup",
        ConfigFileName,
        ConfigFileName + ".backup",
        ThumbnailsFolderName,
        ModelsFolderName,
        LogsFolderName,
    };

    internal static string ResolveDefault()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        return Resolve(appData, GetDebugOverride());
    }

    internal static string Resolve(string appData, string? debugOverride)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(debugOverride))
            return Path.GetFullPath(debugOverride);
#endif

        string companyRoot = Path.Combine(appData, CompanyFolder);
        string newPath = Path.Combine(companyRoot, ProductFolder);
        string legacyPath = Path.Combine(companyRoot, LegacyProductFolder);

        return TryMigrate(companyRoot, newPath, legacyPath);
    }

    /// <summary>
    /// U5-C: one-shot migration of %APPDATA%\Qianlan_App\QL_World\ →
    /// %APPDATA%\Qianlan_App\StellarHub\. Best-effort, NEVER throws —
    /// startup must not crash on migration failures. On any failure falls
    /// back to the legacy path so the user keeps running on their old data
    /// while we retry the migration next launch.
    /// </summary>
    private static string TryMigrate(string companyRoot, string newPath, string legacyPath)
    {
        try
        {
            Directory.CreateDirectory(companyRoot);

            // 1. Already migrated → use new path.
            string markerPath = Path.Combine(newPath, MigrationMarkerFileName);
            if (File.Exists(markerPath))
                return newPath;

            // 2. Legacy folder doesn't exist → fresh install on new path.
            //    No marker needed (cheap re-check on next launch).
            if (!Directory.Exists(legacyPath))
                return newPath;

            // 3. Legacy folder exists but has no known migratable entries →
            //    treat as fresh install on new path.
            if (!HasAnyMigratableEntry(legacyPath))
                return newPath;

            // 4. Migrate each known entry atomically (same-volume File/Directory.Move).
            Directory.CreateDirectory(newPath);
            var moved = new List<(string Src, string Dst, bool IsDir)>();
            Exception? failure = null;
            try
            {
                foreach (string entry in MigratableEntries)
                {
                    string src = Path.Combine(legacyPath, entry);
                    bool srcIsFile = File.Exists(src);
                    bool srcIsDir = Directory.Exists(src);
                    if (!srcIsFile && !srcIsDir) continue;

                    string dst = Path.Combine(newPath, entry);
                    if (srcIsFile)
                        File.Move(src, dst);
                    else
                        Directory.Move(src, dst);
                    moved.Add((src, dst, srcIsDir));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
            {
                // Rollback entries we already moved (best-effort, never throws).
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    var (src, dst, isDir) = moved[i];
                    try
                    {
                        if (isDir)
                            Directory.Move(dst, src);
                        else
                            File.Move(dst, src);
                    }
                    catch
                    {
                        // Best-effort: keep going. We're falling back anyway.
                    }
                }

                // Write a diagnostic log into the legacy folder.
                try
                {
                    File.WriteAllText(
                        Path.Combine(legacyPath, MigrationFailedMarkerFileName),
                        $"{DateTime.UtcNow:o}\n{failure.GetType().Name}: {failure.Message}\n");
                }
                catch
                {
                    // Even logging failed. Still fall back to legacy path.
                }

                // Fallback: run from the legacy path this session.
                return legacyPath;
            }

            // 5. All entries moved successfully → mark and use new path.
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o"));
            return newPath;
        }
        catch
        {
            // Last-resort fallback: never break startup. Use new path; the
            // caller (EnsureDataDirectories) will create what's missing.
            try { Directory.CreateDirectory(newPath); } catch { }
            return newPath;
        }
    }

    private static bool HasAnyMigratableEntry(string legacyPath)
    {
        foreach (string entry in MigratableEntries)
        {
            string path = Path.Combine(legacyPath, entry);
            if (File.Exists(path) || Directory.Exists(path))
                return true;
        }
        return false;
    }

    private static string? GetDebugOverride()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
#else
        return null;
#endif
    }
}
