using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-C: NAS connection persistence + password handling. Mirrors
/// AppSettingsServiceAiTests style (temp dir, IDisposable, AAA).
/// </summary>
public sealed class AppSettingsServiceNasTests : IDisposable
{
    private readonly string _directory;

    public AppSettingsServiceNasTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }

    [Fact]
    public void NasConnections_default_empty_list()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Empty(settings.NasConnections);
    }

    [Fact]
    public void AddNasConnection_assigns_monotonic_id_and_persists()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        int id1 = first.AddNasConnection(new NasConnectionSpec { Name = "nas-1" });
        int id2 = first.AddNasConnection(new NasConnectionSpec { Name = "nas-2" });

        Assert.Equal(1, id1);
        Assert.Equal(2, id2);

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Equal(2, second.NasConnections.Count);
        Assert.Equal("nas-1", second.NasConnections[0].Name);
        Assert.Equal("nas-2", second.NasConnections[1].Name);
    }

    [Fact]
    public void AddNasConnection_does_not_reuse_deleted_id()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        first.AddNasConnection(new NasConnectionSpec { Name = "a" });
        first.AddNasConnection(new NasConnectionSpec { Name = "b" });
        first.AddNasConnection(new NasConnectionSpec { Name = "c" });
        first.DeleteNasConnection(2);

        int newId = first.AddNasConnection(new NasConnectionSpec { Name = "d" });

        // Strictly monotonic: max existing id is 3 → next is 4, not 2.
        Assert.Equal(4, newId);
    }

    [Fact]
    public void UpdateNasConnection_preserves_encrypted_password_and_created_at()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        var spec = new NasConnectionSpec { Name = "n", Host = "old-host" };
        int id = first.AddNasConnection(spec);
        first.SetNasPassword(id, "pw");
        var createdAt = spec.CreatedAt;

        // Mutate Host and call Update.
        spec.Host = "new-host";
        first.UpdateNasConnection(spec);

        var second = new AppSettingsService(_directory);
        second.Load();
        var reloaded = Assert.Single(second.NasConnections);
        Assert.Equal("new-host", reloaded.Host);
        Assert.Equal("pw", second.GetNasPassword(id));
        Assert.Equal(createdAt, reloaded.CreatedAt);
        Assert.True(reloaded.UpdatedAt >= reloaded.CreatedAt);
    }

    [Fact]
    public void UpdateNasConnection_throws_for_unknown_id()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Throws<ArgumentException>(() =>
            settings.UpdateNasConnection(new NasConnectionSpec { Id = 999, Name = "x" }));
    }

    [Fact]
    public void DeleteNasConnection_removes_and_clears_password()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        int id = first.AddNasConnection(new NasConnectionSpec { Name = "n" });
        first.SetNasPassword(id, "secret");

        first.DeleteNasConnection(id);

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Empty(second.NasConnections);
        Assert.False(second.HasNasPassword(id));
        Assert.Null(second.GetNasPassword(id));
        // Plaintext never persisted; encrypted entry also gone because spec removed.
        string json = File.ReadAllText(Path.Combine(_directory, "config.json"));
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void SetNasPassword_encrypts_not_plaintext()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        int id = settings.AddNasConnection(new NasConnectionSpec { Name = "n" });
        const string pw = "plaintext-pw-123";
        settings.SetNasPassword(id, pw);

        string json = File.ReadAllText(Path.Combine(_directory, "config.json"));
        Assert.DoesNotContain(pw, json);
        Assert.Equal(pw, settings.GetNasPassword(id));
    }

    [Fact]
    public void GetNasPassword_returns_null_when_not_set()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        int id = settings.AddNasConnection(new NasConnectionSpec { Name = "n" });
        Assert.Null(settings.GetNasPassword(id));
    }

    [Fact]
    public void SetNasPassword_null_clears_existing()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        int id = settings.AddNasConnection(new NasConnectionSpec { Name = "n" });
        settings.SetNasPassword(id, "x");
        Assert.True(settings.HasNasPassword(id));

        settings.SetNasPassword(id, null);

        Assert.False(settings.HasNasPassword(id));
        Assert.Null(settings.GetNasPassword(id));
    }

    [Fact]
    public void NasConnections_round_trip_across_instances()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        first.AddNasConnection(new NasConnectionSpec
        {
            Name = "synology",
            Protocol = NasConnectionProtocol.WebDav,
            Host = "nas.local",
            Port = 5005,
            RootPath = "/photos/",
            Username = "admin",
            UseTls = false,
            TrustSelfSigned = true,
        });

        var second = new AppSettingsService(_directory);
        second.Load();
        var c = Assert.Single(second.NasConnections);
        Assert.Equal("synology", c.Name);
        Assert.Equal(NasConnectionProtocol.WebDav, c.Protocol);
        Assert.Equal("nas.local", c.Host);
        Assert.Equal(5005, c.Port);
        Assert.Equal("/photos/", c.RootPath);
        Assert.Equal("admin", c.Username);
        Assert.False(c.UseTls);
        Assert.True(c.TrustSelfSigned);
    }

    [Fact]
    public void ThumbnailCacheLimitMb_defaults_1024_and_clamped()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Equal(1024, settings.ThumbnailCacheLimitMb);

        // Below range clamps to 128.
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"thumbnailCacheLimitMb\":5}");
        var low = new AppSettingsService(_directory);
        low.Load();
        Assert.Equal(128, low.ThumbnailCacheLimitMb);

        // Above range clamps to 8192.
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"thumbnailCacheLimitMb\":99999}");
        var high = new AppSettingsService(_directory);
        high.Load();
        Assert.Equal(8192, high.ThumbnailCacheLimitMb);
    }

    [Fact]
    public void UseTls_default_true_round_trips_false()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        var spec = new NasConnectionSpec { Name = "n" }; // UseTls defaults to true
        int id = first.AddNasConnection(spec);
        spec.UseTls = false;
        first.UpdateNasConnection(spec);

        var second = new AppSettingsService(_directory);
        second.Load();
        var c = Assert.Single(second.NasConnections);
        Assert.False(c.UseTls);
    }

    // ─── M8-A: NasRole 枚举 + 强制主 NAS 校验 ───────────────────────────────

    [Fact]
    public void Role_defaults_to_Secondary_for_new_spec()
    {
        var spec = new NasConnectionSpec { Name = "n" };
        Assert.Equal(NasRole.Secondary, spec.Role);
        Assert.False(spec.IsPrimary); // 计算属性同步
    }

    [Fact]
    public void IsPrimary_legacy_field_translates_to_Role_on_deserialize()
    {
        // 旧 v0.2.1 config 只写 isPrimary=true，没有 role 字段
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[{\"id\":1,\"name\":\"legacy\",\"host\":\"h\",\"isPrimary\":true}]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        var c = Assert.Single(settings.NasConnections);
        Assert.Equal(NasRole.Primary, c.Role);
        Assert.True(c.IsPrimary);
    }

    [Fact]
    public void IsPrimary_legacy_false_translates_to_Secondary()
    {
        // 旧 v0.2.1 config 只写 isPrimary=false → 应被识别为 Secondary
        // （ValidateNasRoles 之后第一项会兜底成 Primary，所以测两项）
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[" +
            "{\"id\":1,\"name\":\"a\",\"host\":\"h\",\"isPrimary\":false}," +
            "{\"id\":2,\"name\":\"b\",\"host\":\"h\",\"isPrimary\":true}" +
            "]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        Assert.Equal(2, settings.NasConnections.Count);
        Assert.Equal(NasRole.Secondary, settings.NasConnections[0].Role);
        Assert.Equal(NasRole.Primary, settings.NasConnections[1].Role);
    }

    [Fact]
    public void Load_converges_multiple_Primary_to_exactly_one()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[" +
            "{\"id\":1,\"name\":\"a\",\"host\":\"h\",\"role\":0}," +
            "{\"id\":2,\"name\":\"b\",\"host\":\"h\",\"role\":0}," +
            "{\"id\":3,\"name\":\"c\",\"host\":\"h\",\"role\":0}" +
            "]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        int primaryCount = settings.NasConnections.Count(c => c.Role == NasRole.Primary);
        Assert.Equal(1, primaryCount);
        Assert.Equal(NasRole.Primary, settings.NasConnections[0].Role);
        Assert.Equal(NasRole.Secondary, settings.NasConnections[1].Role);
        Assert.Equal(NasRole.Secondary, settings.NasConnections[2].Role);
    }

    [Fact]
    public void Load_auto_assigns_Primary_to_first_when_none()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[" +
            "{\"id\":1,\"name\":\"a\",\"host\":\"h\",\"role\":1}," +
            "{\"id\":2,\"name\":\"b\",\"host\":\"h\",\"role\":1}" +
            "]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        Assert.Equal(NasRole.Primary, settings.NasConnections[0].Role);
        Assert.Equal(NasRole.Secondary, settings.NasConnections[1].Role);
    }

    [Fact]
    public void Load_forces_ShareLink_no_credentials()
    {
        // ShareLink 配置中误填了 username 和 encryptedPassword → 应被强制清空
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[" +
            "{\"id\":1,\"name\":\"main\",\"host\":\"h\",\"role\":0}," +
            "{\"id\":2,\"name\":\"share\",\"host\":\"h\",\"role\":2," +
            "\"username\":\"bad\",\"encryptedPassword\":\"bad\"}" +
            "]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        var share = settings.NasConnections.First(c => c.Name == "share");
        Assert.Equal(NasRole.ShareLink, share.Role);
        Assert.Equal(string.Empty, share.Username);
        Assert.Null(share.EncryptedPassword);
    }

    [Fact]
    public void Load_empty_list_skips_role_validation()
    {
        // 空列表不应抛异常
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"nasConnections\":[]}");

        var settings = new AppSettingsService(_directory);
        settings.Load();
        Assert.Empty(settings.NasConnections);
    }

    [Fact]
    public void SharedImageRecord_SourceNasId_default_null_and_init()
    {
        var record = new SharedImageRecord
        {
            Id = "hash1",
            FileName = "test.png",
            SourceNasId = 2,
        };
        Assert.Equal(2, record.SourceNasId);

        var legacy = new SharedImageRecord { Id = "hash2", FileName = "old.png" };
        Assert.Null(legacy.SourceNasId); // 旧 library.json 缺该字段时 null
    }
}
