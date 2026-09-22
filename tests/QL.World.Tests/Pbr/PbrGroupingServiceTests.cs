using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;

namespace QL.World.Tests.Pbr;

public sealed class PbrGroupingServiceTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly AppSettingsService _settings;
    private readonly PbrGroupingService _service;

    public PbrGroupingServiceTests()
    {
        _settings = new AppSettingsService(_library.DirectoryRoot);
        _service = new PbrGroupingService(_library.Db, _settings);
    }

    public void Dispose()
    {
        _library.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private string SubDir(string name)
    {
        string path = Path.Combine(_library.DirectoryRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private List<(string Name, long ImageId, string Role)> LoadMembers()
    {
        return _library.Db.Run(conn =>
        {
            var rows = new List<(string, long, string)>();
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT g.name, m.image_id, m.role
                FROM pbr_group_images m JOIN pbr_groups g ON g.id = m.group_id
                ORDER BY g.name, m.image_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            return rows;
        });
    }

    private int CountGroups() => _library.Db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pbr_groups;";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    [Fact]
    public void RescanAll_groups_suffix_sets_and_ignores_singletons()
    {
        string dir = SubDir("tex");
        long baseBare = _library.InsertImage(Path.Combine(dir, "T_Wall.png"));
        long baseColor = _library.InsertImage(Path.Combine(dir, "T_Wall_BaseColor.png"));
        long normal = _library.InsertImage(Path.Combine(dir, "T_Wall_Normal.png"));
        _library.InsertImage(Path.Combine(dir, "T_Wood_D.png"));   // singleton → no group
        _library.InsertImage(Path.Combine(dir, "T_Solo.png"));     // singleton → no group

        PbrRescanReport report = _service.RescanAll();

        Assert.Equal(1, report.GroupsCreated);
        Assert.Equal(1, CountGroups());
        var members = LoadMembers();
        Assert.Equal(3, members.Count);
        Assert.All(members, m => Assert.Equal("T_Wall", m.Name));
        Assert.Equal(
            new[] { baseBare, baseColor, normal }.Order().ToList(),
            members.Select(m => m.ImageId).Order().ToList());
        Assert.Contains(members, m => m.ImageId == baseBare && m.Role == PbrRoles.BaseColor);
        Assert.Contains(members, m => m.ImageId == baseColor && m.Role == PbrRoles.BaseColor);
        Assert.Contains(members, m => m.ImageId == normal && m.Role == PbrRoles.Normal);
    }

    [Fact]
    public void Same_base_in_different_directories_does_not_group()
    {
        string dir1 = SubDir("a");
        string dir2 = SubDir("b");
        _library.InsertImage(Path.Combine(dir1, "T_A_BaseColor.png"));
        _library.InsertImage(Path.Combine(dir2, "T_A_Normal.png"));

        PbrRescanReport report = _service.RescanAll();

        Assert.Equal(0, report.GroupsCreated);
        Assert.Equal(0, CountGroups());
    }

    [Fact]
    public void RescanAll_is_idempotent()
    {
        string dir = SubDir("tex");
        _library.InsertImage(Path.Combine(dir, "T_X_BaseColor.png"));
        _library.InsertImage(Path.Combine(dir, "T_X_Normal.png"));
        _library.InsertImage(Path.Combine(dir, "T_X_Metallic.png"));

        _service.RescanAll();
        var first = LoadMembers();
        int groupsFirst = CountGroups();

        _service.RescanAll();
        Assert.Equal(groupsFirst, CountGroups());
        Assert.Equal(first, LoadMembers());
    }

    [Fact]
    public void RescanAll_removes_groups_that_fall_below_two_members()
    {
        string dir = SubDir("tex");
        long color = _library.InsertImage(Path.Combine(dir, "T_Y_BaseColor.png"));
        long normal = _library.InsertImage(Path.Combine(dir, "T_Y_Normal.png"));
        _service.RescanAll();
        Assert.Equal(1, CountGroups());

        // Directly remove one member row; the rescan must dissolve the group.
        _library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM images WHERE id = $id;";
            command.Parameters.AddWithValue("$id", normal);
            command.ExecuteNonQuery();
        });
        _service.RescanAll();

        Assert.Equal(0, CountGroups());
    }

    [Fact]
    public void RescanDirectories_updates_only_affected_directory()
    {
        string dir1 = SubDir("one");
        string dir2 = SubDir("two");
        _library.InsertImage(Path.Combine(dir1, "T_One_BaseColor.png"));
        _library.InsertImage(Path.Combine(dir1, "T_One_Normal.png"));
        _service.RescanAll();

        // New map pair lands in dir2 only.
        long twoColor = _library.InsertImage(Path.Combine(dir2, "T_Two_BaseColor.png"));
        long twoNormal = _library.InsertImage(Path.Combine(dir2, "T_Two_Normal.png"));

        _service.RescanDirectories([dir2]);

        Assert.Equal(2, CountGroups());
        var members = LoadMembers();
        Assert.Contains(members, m => m.ImageId == twoColor && m.Role == PbrRoles.BaseColor);
        Assert.Contains(members, m => m.ImageId == twoNormal && m.Role == PbrRoles.Normal);
        // dir1 group untouched: its original member pair is still linked.
        Assert.Equal(2, members.Count(m => m.Name == "T_One"));
    }

    [Fact]
    public void RescanDirectories_result_matches_full_rescan()
    {
        string dir1 = SubDir("one");
        string dir2 = SubDir("two");
        _library.InsertImage(Path.Combine(dir1, "T_One_BaseColor.png"));
        _library.InsertImage(Path.Combine(dir1, "T_One_Normal.png"));
        _library.InsertImage(Path.Combine(dir2, "T_Two_Roughness.png"));
        _library.InsertImage(Path.Combine(dir2, "T_Two.png"));

        _service.RescanDirectories([dir1, dir2]);
        var incremental = LoadMembers();

        _service.RescanAll();
        Assert.Equal(incremental, LoadMembers());
    }

    [Fact]
    public void Custom_rules_from_settings_are_applied_and_null_restores_defaults()
    {
        string dir = SubDir("tex");
        _library.InsertImage(Path.Combine(dir, "T_K_XX.png"));
        _library.InsertImage(Path.Combine(dir, "T_K_YY.png"));

        _settings.SetPbrRules(
        [
            new PbrRule("XX", PbrRoles.Normal, true),
            new PbrRule("YY", PbrRoles.Roughness, true),
        ]);

        PbrRescanReport report = _service.RescanAll();

        Assert.Equal(1, report.GroupsCreated);
        var members = LoadMembers();
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.Role == PbrRoles.Normal);
        Assert.Contains(members, m => m.Role == PbrRoles.Roughness);

        // Null/empty restores the built-in defaults.
        _settings.SetPbrRules(null);
        Assert.Equal(PbrRuleSet.Defaults, _settings.PbrRules);
    }

    [Fact]
    public async Task Import_hook_creates_groups_without_blocking_import()
    {
        string dir = SubDir("hook");
        // Distinct sizes keep the three PNGs content-distinct (hash dedupe).
        TestImageFactory.CreatePng(dir, "T_Hook_BaseColor.png", width: 12);
        TestImageFactory.CreatePng(dir, "T_Hook_Normal.png", width: 13);
        TestImageFactory.CreatePng(dir, "T_Lone.png", width: 14);

        var importer = new ImportService(_library.Db, new ImageInspector(), _service);
        ImportReport report = await importer.ImportAsync(new ImportOptions
        {
            InputPaths = [dir],
        });

        Assert.Equal(3, report.AddedCount);
        Assert.Equal(1, CountGroups());
        var members = LoadMembers();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal("T_Hook", m.Name));
    }
}
