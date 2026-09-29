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

    /// <summary>
    /// U2-F (§4.E.2): milestone identifier shown next to the version number on the shell footer.
    /// Settings/About page keeps the pure semantic version (no suffix).
    /// </summary>
    public const string MilestoneLabel = "U4 发布基线";

    private static readonly Version AssemblyVersion =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Major.minor.patch (revision intentionally hidden).</summary>
    public static string Version =>
        $"{AssemblyVersion.Major}.{AssemblyVersion.Minor}.{AssemblyVersion.Build}";
}
