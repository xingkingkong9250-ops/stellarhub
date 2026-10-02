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
            _nasFactory,
            accounts: null,
            currentUser: null);
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

    [Fact]
    public void IsNasManagementEnabled_true_when_currentUser_null()
    {
        // v0.2.1: 未登录场景（首次启动配置 NAS）应可见
        Assert.True(_viewModel.IsNasManagementEnabled);
    }

    [Fact]
    public void SetPrimary_marks_selected_and_clears_others()
    {
        // v0.2.1: 主 NAS 标记——选中项 IsPrimary=true，其他全部 false
        _viewModel.AddNasConnectionCommand.Execute(null); // id=1
        _viewModel.AddNasConnectionCommand.Execute(null); // id=2
        Assert.False(_viewModel.NasConnections[0].IsPrimary);
        Assert.False(_viewModel.NasConnections[1].IsPrimary);

        // 选中第 2 个并设为主
        _viewModel.SelectedNasConnection = _viewModel.NasConnections[1];
        _viewModel.SetPrimaryCommand.Execute(null);

        Assert.False(_viewModel.NasConnections[0].IsPrimary);
        Assert.True(_viewModel.NasConnections[1].IsPrimary);
        Assert.Contains("主 NAS", _viewModel.NasStatusText);
        // 命令在选中项已 IsPrimary=true 时禁用
        Assert.False(_viewModel.SetPrimaryCommand.CanExecute(null));

        // 切换选中第 1 个，命令重新可用
        _viewModel.SelectedNasConnection = _viewModel.NasConnections[0];
        Assert.True(_viewModel.SetPrimaryCommand.CanExecute(null));
        _viewModel.SetPrimaryCommand.Execute(null);
        Assert.True(_viewModel.NasConnections[0].IsPrimary);
        Assert.False(_viewModel.NasConnections[1].IsPrimary);
    }

    [Fact]
    public void SetPrimary_persists_to_settings()
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        _viewModel.SetPrimaryCommand.Execute(null);

        var reloaded = new AppSettingsService(_lib.DirectoryRoot);
        reloaded.Load();
        var c = Assert.Single(reloaded.NasConnections);
        Assert.True(c.IsPrimary);
    }

    // ─── M9-C: SFTP 协议联动（选项/显隐/默认端口/文案） ──────────────────

    [Fact]
    public void NasProtocolOptions_lists_all_three_protocols()
    {
        Assert.Equal(
            new[]
            {
                NasConnectionProtocol.WebDav,
                NasConnectionProtocol.Smb,
                NasConnectionProtocol.Sftp,
            },
            _viewModel.NasProtocolOptions);
    }

    [Fact]
    public void Sftp_selected_hides_TLS_but_shows_trust_checkbox()
    {
        SelectFreshConnection(NasConnectionProtocol.Sftp);

        Assert.True(_viewModel.IsSftpSelected);
        Assert.False(_viewModel.IsTlsApplicable, "SFTP 走 SSH，无独立 TLS 开关");
        Assert.True(_viewModel.IsTrustSelfSignedApplicable, "SFTP 复用信任开关做主机密钥");
    }

    [Fact]
    public void WebDav_selected_shows_TLS_and_trust()
    {
        SelectFreshConnection(NasConnectionProtocol.WebDav);

        Assert.True(_viewModel.IsTlsApplicable);
        Assert.True(_viewModel.IsTrustSelfSignedApplicable);
    }

    [Fact]
    public void Smb_selected_shows_TLS_but_hides_trust()
    {
        SelectFreshConnection(NasConnectionProtocol.Smb);

        Assert.True(_viewModel.IsTlsApplicable);
        Assert.False(_viewModel.IsTrustSelfSignedApplicable);
    }

    [Fact]
    public void Sftp_hints_mention_port_22_volume_root_and_required_username()
    {
        SelectFreshConnection(NasConnectionProtocol.Sftp);

        Assert.Contains("22", _viewModel.NasPortHintText);
        Assert.Contains("/volume1", _viewModel.NasRootPathHint);
        Assert.Contains("必填", _viewModel.NasUsernameHintText);
        Assert.Contains("SSH 主机密钥", _viewModel.NasTrustLabelText);
    }

    [Theory]
    [InlineData(NasConnectionProtocol.WebDav, 443)]
    [InlineData(NasConnectionProtocol.Smb, 445)]
    [InlineData(NasConnectionProtocol.Sftp, 22)]
    public void Protocol_change_from_null_port_fills_default(NasConnectionProtocol protocol, int expectedPort)
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        Assert.Null(_viewModel.SelectedNasConnection!.Port);

        _viewModel.OnNasProtocolChanged(protocol);

        Assert.Equal(expectedPort, _viewModel.SelectedNasConnection.Port);
    }

    [Fact]
    public void Protocol_change_between_known_defaults_overwrites_port()
    {
        SelectFreshConnection(NasConnectionProtocol.WebDav);
        _viewModel.SelectedNasConnection!.Port = 443;

        _viewModel.OnNasProtocolChanged(NasConnectionProtocol.Sftp);
        Assert.Equal(22, _viewModel.SelectedNasConnection.Port);

        _viewModel.OnNasProtocolChanged(NasConnectionProtocol.Smb);
        Assert.Equal(445, _viewModel.SelectedNasConnection.Port);
    }

    [Fact]
    public void Protocol_change_keeps_custom_port()
    {
        SelectFreshConnection(NasConnectionProtocol.WebDav);
        _viewModel.SelectedNasConnection!.Port = 8022;

        _viewModel.OnNasProtocolChanged(NasConnectionProtocol.Sftp);

        Assert.Equal(8022, _viewModel.SelectedNasConnection.Port);
    }

    private void SelectFreshConnection(NasConnectionProtocol protocol)
    {
        _viewModel.AddNasConnectionCommand.Execute(null);
        _viewModel.OnNasProtocolChanged(protocol);
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
