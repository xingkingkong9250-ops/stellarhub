using System.IO;

namespace QL.World.Services;

internal static class AppDataRootResolver
{
    internal const string OverrideEnvironmentVariable = "QL_WORLD_DATA_ROOT";

    internal const string CompanyFolder = "Qianlan_App";
    internal const string ProductFolder = "QL_World";

    internal const string DatabaseFileName = "qlworld.db";
    internal const string ConfigFileName = "config.json";
    internal const string ThumbnailsFolderName = "thumbnails";
    internal const string ModelsFolderName = "models";
    internal const string LogsFolderName = "logs";

    internal static string ResolveDefault()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        return Resolve(appData, GetDebugOverride());
    }

    internal static string Resolve(string appData, string? debugOverride)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(debugOverride))
            return Path.GetFullPath(debugOverride);
#endif

        return Path.Combine(appData, CompanyFolder, ProductFolder);
    }

    private static string? GetDebugOverride()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
#else
        return null;
#endif
    }
}
