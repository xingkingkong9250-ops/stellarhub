using QL.World.Helpers;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-A: <see cref="UserIdentityService"/> Windows contract. Username/DeviceName
/// are populated by <see cref="Environment.UserName"/>/<see cref="Environment.MachineName"/>
/// — both non-empty under any interactive Windows session. macOS/Linux variants
/// are deferred to M6+; these tests pin the Windows baseline.
/// </summary>
public sealed class UserIdentityServiceTests
{
    [Fact]
    public void Username_is_non_empty_on_Windows()
    {
        IUserIdentity id = new UserIdentityService();
        Assert.False(string.IsNullOrWhiteSpace(id.Username));
    }

    [Fact]
    public void DeviceName_is_non_empty_on_Windows()
    {
        IUserIdentity id = new UserIdentityService();
        Assert.False(string.IsNullOrWhiteSpace(id.DeviceName));
    }

    [Fact]
    public void CombinedId_format_is_username_at_devicename()
    {
        IUserIdentity id = new UserIdentityService();
        Assert.Equal($"{id.Username}@{id.DeviceName}", id.CombinedId);
        Assert.Contains("@", id.CombinedId);
    }

    [Fact]
    public void CombinedId_has_no_whitespace_around_separator()
    {
        IUserIdentity id = new UserIdentityService();
        // username and device name on Windows never contain '@' (restricted
        // character for local accounts). The only '@' is the separator.
        int atCount = id.CombinedId.Count(c => c == '@');
        Assert.Equal(1, atCount);
    }
}
