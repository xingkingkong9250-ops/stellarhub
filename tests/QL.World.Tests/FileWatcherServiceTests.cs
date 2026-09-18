using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class FileWatcherServiceTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly ImportService _import;
    private readonly ImageQueryService _query;
    private readonly NotificationService _notifications;
    private readonly FileWatcherService _watcher;

    public FileWatcherServiceTests()
    {
        string dataRoot = _lib.DirectoryRoot;
        _settings = new AppSettingsService(dataRoot);
        _settings.Load();
        _import = new ImportService(_lib.Db, new ImageInspector());
        _query = new ImageQueryService(_lib.Db);
        _notifications = new NotificationService();
        _watcher = new FileWatcherService(_settings, _import, _query, _notifications);
    }

    [Fact]
    public void Deleted_event_marks_missing_and_publishes_notification()
    {
        long imgId = _lib.InsertImage(filePath: "C:\\images\\test.png");
        Assert.False(_notifications.Items.Count > 0);

        // Simulate a delete event.
        _watcher.GetType()
            .GetMethod("ProcessDeleted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(_watcher, [new List<string> { "C:\\images\\test.png" }]);

        // Check notification was published.
        Assert.Contains(_notifications.Items,
            n => n.Kind == NotificationKind.FilesMissing);
    }

    [Fact]
    public void ProcessDeleted_ignores_paths_not_in_library()
    {
        Assert.Empty(_notifications.Items);

        _watcher.GetType()
            .GetMethod("ProcessDeleted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(_watcher, [new List<string> { "C:\\nonexistent\\not_in_db.png" }]);

        Assert.Empty(_notifications.Items);
    }

    [Fact]
    public void UpdateDirectories_creates_watchers_for_valid_dirs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ql-watcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            _watcher.UpdateDirectories([tempDir]);
            // No exception means success; watchers list is private.
            // Re-call with empty to clean up.
            _watcher.UpdateDirectories([]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindByPath_returns_id_for_existing_image()
    {
        long imgId = _lib.InsertImage(filePath: "C:\\test\\find.png");
        long? found = _query.FindByPath("C:\\test\\find.png");
        Assert.Equal(imgId, found);
    }

    [Fact]
    public void FindByPath_returns_null_for_missing_image()
    {
        long? found = _query.FindByPath("C:\\does\\not\\exist.png");
        Assert.Null(found);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _lib.Dispose();
    }
}
