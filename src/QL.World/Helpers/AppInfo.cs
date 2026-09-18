using System.Reflection;

namespace QL.World.Helpers;

/// <summary>
/// Single source for product identity shown in the UI.
/// The numeric version comes from the csproj &lt;Version&gt; property only.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "QL World";

    public const string ProductSubtitle = "3D 美术图片素材管理系统";

    private static readonly Version AssemblyVersion =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Major.minor.patch (revision intentionally hidden).</summary>
    public static string Version =>
        $"{AssemblyVersion.Major}.{AssemblyVersion.Minor}.{AssemblyVersion.Build}";
}
