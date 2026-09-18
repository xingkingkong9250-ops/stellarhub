using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class AppDataRootResolverTests
{
    [Fact]
    public void Current_build_respects_override_only_in_debug()
    {
        string appData = Path.GetFullPath(
            Path.Combine("test-data", "real-appdata"));
        string isolatedRoot = Path.GetFullPath(
            Path.Combine("test-data", "isolated"));

        string result = AppDataRootResolver.Resolve(appData, isolatedRoot);

#if DEBUG
        Assert.Equal(isolatedRoot, result);
#else
        Assert.Equal(
            Path.Combine(appData, "Qianlan_App", "QL_World"),
            result);
#endif
    }

    [Fact]
    public void Missing_debug_override_uses_real_appdata_location()
    {
        string appData = Path.GetFullPath(
            Path.Combine("test-data", "real-appdata"));

        string result = AppDataRootResolver.Resolve(appData, "   ");

        Assert.Equal(
            Path.Combine(appData, "Qianlan_App", "QL_World"),
            result);
    }
}
