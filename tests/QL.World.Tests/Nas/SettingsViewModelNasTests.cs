using System.IO;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-C: NAS tab ViewModel commands + connection lifecycle. Mirrors
/// SettingsViewModelTests bootstrap (TestLibrary + real services) but
/// injects the StubNasConnectorFactory as the last SettingsViewModel arg.
/// </summary>
public sealed class SettingsViewModelNasTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly ThumbnailService _thumbnails;
    private readonly AppSettingsService _settings;
    private readonly NotificationService _notifications = new();
    private readonly StubNasConnectorFactory _nasFactory = new();
    private readonly SettingsViewModel _viewModel;

    public SettingsViewModelNasTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _thumbnails = new ThumbnailService(_lib.DirectoryRoot, _settings);
        var imageQuery = new ImageQueryService(_lib.Db);
        var import = new ImportService(_lib.Db, new ImageInspector());
        var watcher = new FileWatcherService(_settings, import, imageQuery, _notifications);

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
            _nasFactory);
    }

    [Fact]
    public void AddNasConnection_adds_to_list_and_selects()
    {
        Assert.Empty(_viewModel.NasConnections);

        _viewModel.AddNasConnectionCommand.Execute(null);

        var single = Assert.Single(_viewModel.NasConnections);
        Assert.Equal("新连接", single.Name);
        Assert.Equal(1, single.Id);
        Assert.Same(single, _viewModel.SelectedNasConnection);
    }

    [Fact]
    public void DeleteNasConnection_removes_selected()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        Assert.Single(_viewModel.NasConnections);

        _viewModel.DeleteNasConnectionCommand.Execute(null);

        Assert.Empty(_viewModel.NasConnections);
        Assert.Null(_viewModel.SelectedNasConnection);
    }

    [Fact]
    public void SaveNasConnection_persists_and_updates_status()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        var selected = _viewModel.SelectedNasConnection!;
        selected.Name = "我的 NAS";

        _viewModel.SaveNasConnectionCommand.Execute(null);

        Assert.Contains("已保存", _viewModel.NasStatusText);

        var reloaded = new AppSettingsService(_lib.DirectoryRoot);
        reloaded.Load();
        var c = Assert.Single(reloaded.NasConnections);
        Assert.Equal("我的 NAS", c.Name);
    }

    [Fact]
    public void SetNasPasswordInput_persists_via_settings()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        int id = _viewModel.SelectedNasConnection!.Id;

        _viewModel.SetNasPasswordInput(id, "topsecret");

        Assert.True(_settings.HasNasPassword(id));
        Assert.Equal("topsecret", _settings.GetNasPassword(id));
    }

    [Fact]
    public async Task TestNasConnectionAsync_success_reports_and_disposes()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        _nasFactory.NextSuccess = true;

        await _viewModel.TestNasConnectionCommand.ExecuteAsync();

        Assert.Contains("连接成功", _viewModel.NasStatusText);
        Assert.True(_nasFactory.LastConnectorDisposed);
        Assert.False(_viewModel.IsTestingNasConnection);
    }

    [Fact]
    public async Task TestNasConnectionAsync_failure_reports_kind()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        _nasFactory.NextException =
            new NasConnectionException(NasConnectionKind.AuthenticationFailed, "401");

        await _viewModel.TestNasConnectionCommand.ExecuteAsync();

        Assert.Contains("AuthenticationFailed", _viewModel.NasStatusText);
        Assert.False(_viewModel.IsTestingNasConnection);
    }

    [Fact]
    public void Delete_command_disabled_without_selection()
    {
        Assert.False(_viewModel.DeleteNasConnectionCommand.CanExecute(null));

        _viewModel.AddNasConnectionCommand.Execute(null);

        Assert.True(_viewModel.DeleteNasConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Test_command_enabled_when_selection_present_and_not_testing()
    {
        Assert.False(_viewModel.TestNasConnectionCommand.CanExecute(null));

        _viewModel.AddNasConnectionCommand.Execute(null);

        Assert.True(_viewModel.TestNasConnectionCommand.CanExecute(null));
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
