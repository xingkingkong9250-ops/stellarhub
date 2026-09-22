namespace QL.World.Models;

/// <summary>Fixed vocabulary for <see cref="PbrRule.Role"/> values (schema v2 pbr_group_images.role).</summary>
public static class PbrRoles
{
    public const string BaseColor = "basecolor";
    public const string Normal = "normal";
    public const string Roughness = "roughness";
    public const string Metallic = "metallic";
    public const string Ao = "ao";
    public const string Height = "height";
    public const string Other = "other";
}

/// <summary>
/// One editable PBR grouping rule: a filename suffix (without extension)
/// mapped to a texture role. Serialized into config.json (pbrRules).
/// </summary>
public sealed record PbrRule(string Pattern, string Role, bool Enabled);
