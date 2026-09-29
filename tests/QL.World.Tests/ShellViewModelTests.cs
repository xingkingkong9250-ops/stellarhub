using System.IO;
using System.Text.RegularExpressions;
using QL.World.Data;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Colors;
using QL.World.Services.Pbr;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _directory;
    private readonly DatabaseService _database;
    private readonly ThumbnailService _thumbnails;
    private readonly ShellViewModel _viewModel;

    public ShellViewModelTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var settings = new AppSettingsService(_directory);
        settings.Load();

        _database = new DatabaseService(_directory);
        _database.Initialize();

        var inspector = new ImageInspector();
        var import = new ImportService(_database, inspector);
        _thumbnails = new ThumbnailService(_directory, settings);
        var imageQuery = new ImageQueryService(_database);
        var tags = new TagService(_database);
        var collections = new CollectionService(_database);
        var notifications = new NotificationService();
        var watcher = new FileWatcherService(settings, import, imageQuery, notifications);
        var legacyImporter = new LegacyPythonImporter(_database);

        var ai = new DisabledAiService();
        var aiAnalysis = new AiAnalysisService(ai, _database, tags, notifications);
        var colors = new ColorAnalysisService(_database);
        var semanticSearch = new SemanticSearchService(_database, ai);
        var pbrGrouping = new PbrGroupingService(_database, settings);
        var services = new ServiceRegistry(
            settings, _database, inspector, import, _thumbnails, imageQuery,
            tags, collections, notifications, watcher, legacyImporter, ai, aiAnalysis,
            colors, semanticSearch, pbrGrouping);
        _viewModel = new ShellViewModel(services);
    }

    [Fact]
    public void Title_contains_product_name()
    {
        Assert.Contains("QL World", _viewModel.Title);
    }

    [Fact]
    public void Version_text_uses_three_part_version_from_assembly()
    {
        // U2-F (§4.E.2): shell footer appends the milestone label after the semantic version.
        Assert.Matches(
            new Regex(@"^v\d+\.\d+\.\d+ · .+$"),
            _viewModel.VersionText);
    }

    [Fact]
    public void M1_status_strings_are_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(_viewModel.StatusBar.StatusText));
        Assert.False(string.IsNullOrWhiteSpace(_viewModel.AiStatusText));
        Assert.False(string.IsNullOrWhiteSpace(_viewModel.StatusBar.AiStatusText));
    }

    [Fact]
    public void Gallery_starts_empty_with_default_view_mode()
    {
        Assert.Empty(_viewModel.Gallery.Items);
        Assert.True(
            _viewModel.Gallery.IsGridView || _viewModel.Gallery.IsListView);
    }

    // ─── U3-B (§4.C.1): B2-3-ACT hover action commands ─────────────────────

    [Fact]
    public void Hover_action_commands_require_a_non_missing_item_parameter()
    {
        var present = MakeItem(isMissing: false);
        var missing = MakeItem(isMissing: true);

        foreach (var command in new[]
        {
            _viewModel.OpenItemCommand,
            _viewModel.RevealItemCommand,
            _viewModel.CopyItemPathCommand,
        })
        {
            Assert.True(command.CanExecute(present));
            Assert.False(command.CanExecute(missing));
            Assert.False(command.CanExecute(null));
            Assert.False(command.CanExecute("not-an-item"));
        }
    }

    [Fact]
    public void Hover_action_commands_tolerate_null_or_wrong_type_parameters()
    {
        // Execute must be a no-op (never throw) for invalid parameters;
        // side-effecting routes (Navigator/Clipboard) are not covered here.
        _viewModel.OpenItemCommand.Execute(null);
        _viewModel.RevealItemCommand.Execute(null);
        _viewModel.CopyItemPathCommand.Execute(null);
        _viewModel.OpenItemCommand.Execute(42);
    }

    private static GalleryItemViewModel MakeItem(bool isMissing) => new(new GalleryListItem
    {
        Id = 1,
        FileName = "a.png",
        FilePath = @"X:\a.png",
        FileHash = "h",
        FileSize = 1,
        Width = 4,
        Height = 4,
        Format = "png",
        Rating = 0,
        PrimaryCategory = null,
        IsMissing = isMissing,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TagsCsv = string.Empty,
        SourceBadge = null,
    });

    public void Dispose()
    {
        try { _viewModel.Dispose(); }
        catch (IOException) { /* Best effort. */ }

        try { _thumbnails.Dispose(); }
        catch (IOException) { /* Best effort. */ }

        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }
}
