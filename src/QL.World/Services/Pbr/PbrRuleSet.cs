using QL.World.Models;

namespace QL.World.Services.Pbr;

/// <summary>
/// Default PBR suffix rules and the shared suffix-stripping matcher.
/// Matching is first-match, case-insensitive, anchored at the end of the
/// file name (extension already removed). A pattern starting with a
/// separator ("_", "-", ".", space) may directly follow a letter/digit
/// ("Wall_D" matches "_D"); otherwise the character before the match must
/// not be a letter/digit ("Abnormal" does not match "Normal").
/// </summary>
public static class PbrRuleSet
{
    /// <summary>Built-in defaults; longest/most specific patterns first.</summary>
    public static IReadOnlyList<PbrRule> Defaults { get; } =
    [
        new("BaseColor", PbrRoles.BaseColor, true),
        new("Albedo", PbrRoles.BaseColor, true),
        new("Diffuse", PbrRoles.BaseColor, true),
        new("_D", PbrRoles.BaseColor, true),
        new("Normal", PbrRoles.Normal, true),
        new("_N", PbrRoles.Normal, true),
        new("Roughness", PbrRoles.Roughness, true),
        new("Rough", PbrRoles.Roughness, true),
        new("Metallic", PbrRoles.Metallic, true),
        new("Metalness", PbrRoles.Metallic, true),
        new("Metal", PbrRoles.Metallic, true),
        new("AO", PbrRoles.Ao, true),
        new("Occlusion", PbrRoles.Ao, true),
        new("Height", PbrRoles.Height, true),
        new("Bump", PbrRoles.Height, true),
        new("Displacement", PbrRoles.Height, true),
        new("Disp", PbrRoles.Height, true),
    ];

    /// <summary>
    /// Strips the first matching suffix from <paramref name="fileNameWithoutExtension"/>.
    /// Returns null when no enabled rule matches or stripping would empty the base name.
    /// </summary>
    public static (string BaseName, string Role)? MatchRole(
        string fileNameWithoutExtension,
        IReadOnlyList<PbrRule> rules)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            return null;

        foreach (PbrRule rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern))
                continue;

            string pattern = rule.Pattern;
            if (fileNameWithoutExtension.Length <= pattern.Length)
                continue;
            if (!fileNameWithoutExtension.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                continue;

            int matchStart = fileNameWithoutExtension.Length - pattern.Length;
            bool boundaryOk = matchStart == 0
                || !char.IsLetterOrDigit(fileNameWithoutExtension[matchStart - 1])
                || IsSeparator(pattern[0]);
            if (!boundaryOk)
                continue;

            string baseName = fileNameWithoutExtension[..matchStart]
                .TrimEnd('_', '-', ' ', '.');
            if (baseName.Length == 0)
                continue;

            return (baseName, rule.Role);
        }

        return null;
    }

    private static bool IsSeparator(char c) =>
        c is '_' or '-' or '.' or ' ';
}
