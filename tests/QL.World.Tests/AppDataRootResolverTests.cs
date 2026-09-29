using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

/// <summary>
/// U5-C: brand migration from %APPDATA%\Qianlan_App\QL_World\ to
/// %APPDATA%\Qianlan_App\StellarHub\. Migration is best-effort, idempotent,
/// and never breaks startup — on failure it falls back to the legacy path.
/// </summary>
public sealed class AppDataRootResolverTests
{
    [Fact]
    public void Debug_override_takes_precedence_over_migration()
    {
        // DEBUG build: a non-empty debug override short-circuits Resolve() before
        // the migration branch runs, so isolated test roots stay isolated.
        string appData = Path.GetFullPath(Path.Combine("test-data", "real-appdata"));
        string isolatedRoot = Path.GetFullPath(Path.Combine("test-data", "isolated"));

        string result = AppDataRootResolver.Resolve(appData, isolatedRoot);

#if DEBUG
        Assert.Equal(isolatedRoot, result);
#else
        Assert.Equal(
            Path.Combine(appData, AppDataRootResolver.CompanyFolder, AppDataRootResolver.ProductFolder),
            result);
#endif
    }

    [Fact]
    public void Fresh_install_no_legacy_returns_new_path_without_marker()
    {
        string appData = NewScratchAppData();
        try
        {
            string result = AppDataRootResolver.Resolve(appData, null);

            string expected = Path.Combine(
                appData, AppDataRootResolver.CompanyFolder, AppDataRootResolver.ProductFolder);
            Assert.Equal(expected, result);
            // No legacy → no migration marker needed.
            Assert.False(Directory.Exists(expected) &&
                File.Exists(Path.Combine(expected, AppDataRootResolver.MigrationMarkerFileName)));
        }
        finally
        {
            TryDelete(appData);
        }
    }

    [Fact]
    public void Empty_legacy_folder_treated_as_fresh_install()
    {
        string appData = NewScratchAppData();
        string companyRoot = Path.Combine(appData, AppDataRootResolver.CompanyFolder);
        string legacyPath = Path.Combine(companyRoot, AppDataRootResolver.LegacyProductFolder);
        Directory.CreateDirectory(legacyPath);
        try
        {
            string result = AppDataRootResolver.Resolve(appData, null);

            string expected = Path.Combine(companyRoot, AppDataRootResolver.ProductFolder);
            Assert.Equal(expected, result);
            // Legacy empty → migration skipped, no marker.
            Assert.False(File.Exists(Path.Combine(expected, AppDataRootResolver.MigrationMarkerFileName)));
        }
        finally
        {
            TryDelete(appData);
        }
    }

    [Fact]
    public void First_migration_moves_legacy_entries_and_writes_marker()
    {
        string appData = NewScratchAppData();
        string companyRoot = Path.Combine(appData, AppDataRootResolver.CompanyFolder);
        string legacyPath = Path.Combine(companyRoot, AppDataRootResolver.LegacyProductFolder);
        string newPath = Path.Combine(companyRoot, AppDataRootResolver.ProductFolder);
        Directory.CreateDirectory(legacyPath);
        File.WriteAllText(Path.Combine(legacyPath, AppDataRootResolver.DatabaseFileName), "fake db");
        Directory.CreateDirectory(Path.Combine(legacyPath, AppDataRootResolver.ThumbnailsFolderName));
        Directory.CreateDirectory(Path.Combine(legacyPath, AppDataRootResolver.ModelsFolderName));
        try
        {
            string result = AppDataRootResolver.Resolve(appData, null);

            Assert.Equal(newPath, result);
            // Marker written.
            Assert.True(File.Exists(Path.Combine(newPath, AppDataRootResolver.MigrationMarkerFileName)));
            // All entries migrated to new path.
            Assert.True(File.Exists(Path.Combine(newPath, AppDataRootResolver.DatabaseFileName)));
            Assert.True(Directory.Exists(Path.Combine(newPath, AppDataRootResolver.ThumbnailsFolderName)));
            Assert.True(Directory.Exists(Path.Combine(newPath, AppDataRootResolver.ModelsFolderName)));
            // All entries gone from legacy path.
            Assert.False(File.Exists(Path.Combine(legacyPath, AppDataRootResolver.DatabaseFileName)));
            Assert.False(Directory.Exists(Path.Combine(legacyPath, AppDataRootResolver.ThumbnailsFolderName)));
            Assert.False(Directory.Exists(Path.Combine(legacyPath, AppDataRootResolver.ModelsFolderName)));
        }
        finally
        {
            TryDelete(appData);
        }
    }

    [Fact]
    public void Idempotent_marker_exists_skips_migration()
    {
        string appData = NewScratchAppData();
        string companyRoot = Path.Combine(appData, AppDataRootResolver.CompanyFolder);
        string legacyPath = Path.Combine(companyRoot, AppDataRootResolver.LegacyProductFolder);
        string newPath = Path.Combine(companyRoot, AppDataRootResolver.ProductFolder);
        // Pre-seed: new path already migrated, plus a marker.
        Directory.CreateDirectory(newPath);
        File.WriteAllText(Path.Combine(newPath, AppDataRootResolver.MigrationMarkerFileName), "2026-09-29T00:00:00Z");
        string existingFile = Path.Combine(newPath, "preexisting.txt");
        File.WriteAllText(existingFile, "must not be touched");
        // Legacy still has an entry that would be moved if migration ran.
        Directory.CreateDirectory(legacyPath);
        File.WriteAllText(Path.Combine(legacyPath, AppDataRootResolver.DatabaseFileName), "must not move");

        try
        {
            string result = AppDataRootResolver.Resolve(appData, null);

            Assert.Equal(newPath, result);
            // Legacy entry stays put (migration was skipped).
            Assert.True(File.Exists(Path.Combine(legacyPath, AppDataRootResolver.DatabaseFileName)));
            // Pre-existing new-path content untouched.
            Assert.True(File.Exists(existingFile));
        }
        finally
        {
            TryDelete(appData);
        }
    }

    [Fact]
    public void Migration_failure_falls_back_to_legacy_writes_failure_marker()
    {
        string appData = NewScratchAppData();
        string companyRoot = Path.Combine(appData, AppDataRootResolver.CompanyFolder);
        string legacyPath = Path.Combine(companyRoot, AppDataRootResolver.LegacyProductFolder);
        string newPath = Path.Combine(companyRoot, AppDataRootResolver.ProductFolder);
        Directory.CreateDirectory(legacyPath);
        string dbPath = Path.Combine(legacyPath, AppDataRootResolver.DatabaseFileName);
        File.WriteAllText(dbPath, "locked source");

        try
        {
            // Hold an exclusive lock on the source file — File.Move will throw
            // IOException when it cannot delete the source.
            using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string result = AppDataRootResolver.Resolve(appData, null);

                // Fallback to legacy path this session — startup stays alive.
                Assert.Equal(legacyPath, result);
                // Failure diagnostic written into legacy folder.
                Assert.True(File.Exists(
                    Path.Combine(legacyPath, AppDataRootResolver.MigrationFailedMarkerFileName)));
                // No success marker (so next launch will retry).
                Assert.False(File.Exists(
                    Path.Combine(newPath, AppDataRootResolver.MigrationMarkerFileName)));
                // Source still in legacy (still locked, never moved).
                Assert.True(File.Exists(dbPath));
                // Nothing landed in new path.
                if (Directory.Exists(newPath))
                    Assert.False(File.Exists(Path.Combine(newPath, AppDataRootResolver.DatabaseFileName)));
            }
        }
        finally
        {
            TryDelete(appData);
        }
    }

    private static string NewScratchAppData() =>
        Path.Combine(Path.GetTempPath(), "qlworld-appdata-test-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
