using System.IO;
using QL.World.Helpers;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class FirstRunWizardViewModelTests : IDisposable
{
    private readonly string _workRoot;
    private readonly string _settingsDir;
    private readonly string _dataRoot;
    private readonly string _libraryRoot;
    private readonly TestLibrary _dbLibrary;
    private readonly AppSettingsService _settings;

    public FirstRunWizardViewModelTests()
    {
        _workRoot = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        _settingsDir = Path.Combine(_workRoot, "config");
        _dataRoot = Path.Combine(_workRoot, "data");
        _libraryRoot = Path.Combine(_workRoot, "library");
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_dataRoot);
        _dbLibrary = new TestLibrary();
        _settings = new AppSettingsService(_settingsDir);
        _settings.Load();
    }

    public void Dispose()
    {
        _dbLibrary.Dispose();
        try { Directory.Delete(_workRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private FirstRunWizardViewModel CreateVm(bool hasLegacyData, Func<string?> picker)
        => new(_settings, _dbLibrary.Db, _dataRoot, hasLegacyData, picker);

    [Fact]
    public void Buttons_block_until_a_valid_folder_is_chosen()
    {
        var vm = CreateVm(hasLegacyData: false, picker: () => null);

        Assert.False(vm.CanProceed);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(vm.MigrateNowCommand.CanExecute(null));
        Assert.False(vm.SkipCommand.CanExecute(null));
    }

    [Fact]
    public void Null_picker_keeps_path_unchanged()
    {
        var vm = CreateVm(false, () => null);
        vm.BrowseCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SelectedLibraryPath);
        Assert.False(vm.CanProceed);
    }

    [Fact]
    public void New_install_confirm_persists_root_without_skip_flag()
    {
        var vm = CreateVm(false, () => _libraryRoot);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.BrowseCommand.Execute(null);
        Assert.True(vm.CanProceed);
        Assert.False(vm.HasValidationError);

        vm.ConfirmCommand.Execute(null);

        Assert.True(_settings.IsLibraryRootConfigured);
        Assert.Equal(Path.GetFullPath(_libraryRoot), _settings.LibraryRootPath);
        Assert.False(_settings.LegacyDataSkipped);
        Assert.Null(vm.MigrationDecision);
        Assert.True(closed);
    }

    [Fact]
    public void Cross_volume_selection_shows_error_and_blocks_completion()
    {
        var vm = CreateVm(false, () => @"D:\ql-wizard-library");

        vm.BrowseCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Contains("盘符", vm.ValidationError);
        Assert.False(vm.CanProceed);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(_settings.IsLibraryRootConfigured);
    }

    [Fact]
    public void Legacy_skip_persists_root_and_sets_soft_skip_flag()
    {
        var vm = CreateVm(true, () => _libraryRoot);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.BrowseCommand.Execute(null);
        vm.SkipCommand.Execute(null);

        Assert.True(_settings.IsLibraryRootConfigured);
        Assert.True(_settings.LegacyDataSkipped);
        Assert.False(vm.MigrationDecision);
        Assert.True(closed);
    }

    [Fact]
    public void Legacy_migrate_copies_bytes_then_closes_with_decision_true()
    {
        // One pre-M11 local row pointing at a real file in the work area.
        string sourceDir = Path.Combine(_workRoot, "old-pictures");
        string sourceFile = TestImageFactory.CreatePng(sourceDir, "old.png");
        string hash = Md5Hasher.ComputeFile(sourceFile);
        long id = _dbLibrary.InsertImage(
            filePath: sourceFile, hash: hash, category: "角色");

        var vm = CreateVm(true, () => _libraryRoot);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.BrowseCommand.Execute(null);
        Assert.True(vm.MigrateNowCommand.CanExecute(null));
        vm.MigrateNowCommand.Execute(null);

        Assert.True(closed);
        Assert.True(vm.MigrationDecision);
        Assert.True(_settings.IsLibraryRootConfigured);
        Assert.False(_settings.LegacyDataSkipped);
        Assert.False(string.IsNullOrEmpty(vm.MigrationStatus));

        string? libraryPath = _dbLibrary.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT library_path FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        });
        Assert.NotNull(libraryPath);
        Assert.True(File.Exists(libraryPath));
    }
}
