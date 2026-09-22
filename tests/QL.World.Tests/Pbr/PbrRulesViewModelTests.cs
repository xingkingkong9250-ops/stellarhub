using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class PbrRulesViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly PbrGroupingService _grouping;

    public PbrRulesViewModelTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _grouping = new PbrGroupingService(_lib.Db, _settings);
    }

    private int CountGroups() => _lib.Db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pbr_groups;";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    private string SeedTexDirectory(string name)
    {
        string dir = Path.Combine(_lib.DirectoryRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Constructor_loads_active_rules_into_rows()
    {
        var vm = new PbrRulesViewModel(_settings, _grouping);

        Assert.Equal(_settings.PbrRules.Count, vm.Rules.Count);
        Assert.Equal(PbrRuleSet.Defaults[0].Pattern, vm.Rules[0].Pattern);
        Assert.All(vm.Rules, r => Assert.Contains(r.Role, vm.RoleOptions.Select(o => o.Value)));
    }

    [Fact]
    public void Add_and_save_persist_custom_rules_and_rescan()
    {
        string dir = SeedTexDirectory("tex");
        _lib.InsertImage(Path.Combine(dir, "T_K_XX.png"));
        _lib.InsertImage(Path.Combine(dir, "T_K_YY.png"));

        var vm = new PbrRulesViewModel(_settings, _grouping);
        while (vm.Rules.Count > 0) // 清掉默认 17 条，只测自定义两条
            vm.Rules[0].RemoveCommand.Execute(null);
        vm.AddRuleCommand.Execute(null);
        vm.Rules[^1].Pattern = "XX";
        vm.Rules[^1].Role = PbrRoles.Normal;
        vm.AddRuleCommand.Execute(null);
        vm.Rules[^1].Pattern = "YY";
        vm.Rules[^1].Role = PbrRoles.Roughness;

        vm.SaveCommand.Execute(null);

        IReadOnlyList<PbrRule> saved = _settings.PbrRules;
        Assert.Equal(2, saved.Count);
        Assert.Equal("XX", saved[0].Pattern);
        Assert.Equal(PbrRoles.Normal, saved[0].Role);
        Assert.Equal(1, CountGroups());
        Assert.Contains("已保存 2 条规则", vm.StatusText);
    }

    [Fact]
    public void Save_with_only_blank_patterns_restores_defaults()
    {
        var vm = new PbrRulesViewModel(_settings, _grouping);
        while (vm.Rules.Count > 0) // 清掉默认行后只剩一条空白行
            vm.Rules[0].RemoveCommand.Execute(null);
        vm.AddRuleCommand.Execute(null); // blank pattern row

        vm.SaveCommand.Execute(null);

        Assert.Equal(PbrRuleSet.Defaults, _settings.PbrRules);
        Assert.Contains("恢复", vm.StatusText);
    }

    [Fact]
    public void Restore_defaults_clears_custom_rules_and_rebuilds()
    {
        _settings.SetPbrRules([new PbrRule("XX", PbrRoles.Normal, true)]);
        string dir = SeedTexDirectory("tex");
        _lib.InsertImage(Path.Combine(dir, "T_D_BaseColor.png"));
        _lib.InsertImage(Path.Combine(dir, "T_D_Normal.png"));

        var vm = new PbrRulesViewModel(_settings, _grouping);
        Assert.Single(vm.Rules);

        vm.RestoreDefaultsCommand.Execute(null);

        Assert.Equal(PbrRuleSet.Defaults, _settings.PbrRules);
        Assert.Equal(PbrRuleSet.Defaults.Count, vm.Rules.Count);
        Assert.Equal(1, CountGroups());
        Assert.Contains("恢复默认", vm.StatusText);
    }

    [Fact]
    public void Rescan_command_rebuilds_groups()
    {
        string dir = SeedTexDirectory("tex");
        _lib.InsertImage(Path.Combine(dir, "T_R_BaseColor.png"));
        _lib.InsertImage(Path.Combine(dir, "T_R_Normal.png"));
        Assert.Equal(0, CountGroups());

        var vm = new PbrRulesViewModel(_settings, _grouping);
        vm.RescanCommand.Execute(null);

        Assert.Equal(1, CountGroups());
        Assert.Contains("重建完成", vm.StatusText);
    }

    [Fact]
    public void Remove_row_removes_exactly_one_row()
    {
        var vm = new PbrRulesViewModel(_settings, _grouping);
        int before = vm.Rules.Count;

        vm.Rules[0].RemoveCommand.Execute(null);

        Assert.Equal(before - 1, vm.Rules.Count);
    }

    public void Dispose()
    {
        _lib.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
