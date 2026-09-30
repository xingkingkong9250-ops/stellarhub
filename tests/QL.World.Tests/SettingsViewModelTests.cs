using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly ThumbnailService _thumbnails;
    private readonly SettingsViewModel _viewModel;
    private readonly AppSettingsService _settings;
    private readonly NotificationService _notifications = new();
    private readonly StubNasConnectorFactory _nasFactory = new();

    public SettingsViewModelTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _thumbnails = new ThumbnailService(_lib.DirectoryRoot, _settings);
        var imageQuery = new ImageQueryService(_lib.Db);
        var import = new ImportService(_lib.Db, new ImageInspector());
        var watcher = new FileWatcherService(
            _settings, import, imageQuery, _notifications);

        _viewModel = new SettingsViewModel(
            _settings,
            _thumbnails,
            imageQuery,
            watcher,
            new LegacyPythonImporter(_lib.Db),
            new ModelDownloadService(_lib.DirectoryRoot),
            _notifications,
            Path.Combine(_lib.DirectoryRoot, "logs"),
            new PbrGroupingService(_lib.Db, _settings),
            _nasFactory,
            accounts: null,
            currentUser: null);
    }

    [Fact]
    public void Statistics_text_reflects_library_counts()
    {
        Assert.Contains("0 张", _viewModel.StatisticsText);

        _lib.InsertImage();
        _lib.InsertImage();
        var query = new ImageQueryService(_lib.Db);
        query.SetMissing(2, isMissing: true);

        // Statistics refresh via constructor path; call through legacy import
        // side effects indirectly — simplest is to construct again.
        var fresh = new SettingsViewModel(
            _settings, _thumbnails, query,
            new FileWatcherService(_settings,
                new ImportService(_lib.Db, new ImageInspector()),
                query, _notifications),
            new LegacyPythonImporter(_lib.Db),
            new ModelDownloadService(_lib.DirectoryRoot),
            _notifications,
            Path.Combine(_lib.DirectoryRoot, "logs"),
            new PbrGroupingService(_lib.Db, _settings),
            _nasFactory,
            accounts: null,
            currentUser: null);

        Assert.Equal("图片 2 张，其中缺失 1 张", fresh.StatisticsText);
    }

    [Fact]
    public void Theme_change_persists_and_raises_event()
    {
        string? raised = null;
        _viewModel.ThemeChanged += theme => raised = theme;

        _viewModel.SelectedTheme = "Light";

        Assert.Equal("Light", raised);
        Assert.Equal("Light", _settings.Theme);

        // A fresh service reads the persisted value.
        var reloaded = new AppSettingsService(_lib.DirectoryRoot);
        reloaded.Load();
        Assert.Equal("Light", reloaded.Theme);
    }

    [Fact]
    public void Watcher_directories_add_remove_persist()
    {
        _viewModel.WatcherDirectories.Add(@"X:\watched");
        _viewModel.RemoveWatcherDirectoryCommand.Execute(null); // nothing selected

        _viewModel.SelectedWatcherDirectory = @"X:\watched";
        _viewModel.RemoveWatcherDirectoryCommand.Execute(null);

        Assert.Empty(_viewModel.WatcherDirectories);
    }

    [Fact]
    public void Remove_command_disabled_without_selection()
    {
        Assert.False(_viewModel.RemoveWatcherDirectoryCommand.CanExecute(null));
        _viewModel.WatcherDirectories.Add(@"X:\watched");
        _viewModel.SelectedWatcherDirectory = @"X:\watched";
        Assert.True(_viewModel.RemoveWatcherDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Legacy_import_reports_missing_database()
    {
        _viewModel.LegacyDbPath = @"X:\no-such\ql_world.db";

        await _viewModel.RunLegacyImportCommand.ExecuteAsync();

        Assert.Contains("不存在", _viewModel.LegacyStatusText);
        Assert.False(_viewModel.IsLegacyImporting);
    }

    [Fact]
    public async Task Legacy_import_success_updates_status_and_publishes_notification()
    {
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\one.png", "hash-1")
            .AddImage(@"X:\py\two.png", "hash-2");
        _viewModel.LegacyDbPath = legacy.DbPath;

        await _viewModel.RunLegacyImportCommand.ExecuteAsync();

        Assert.Contains("新增 2", _viewModel.LegacyStatusText);
        Assert.Contains("跳过 0", _viewModel.LegacyStatusText);
        Assert.False(_viewModel.IsLegacyImporting);

        Assert.Equal(1, _notifications.UnreadCount);
        Assert.Equal(
            NotificationKind.LegacyImportCompleted,
            _notifications.Items[0].Kind);
    }

    [Fact]
    public void Version_text_is_three_part()
    {
        Assert.Matches(@"^v\d+\.\d+\.\d+", _viewModel.VersionText);
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
