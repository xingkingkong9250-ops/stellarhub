using System.Text.Json.Serialization;

namespace QL.World.Models;

/// <summary>
/// M6-A: a single application account stored in NAS-shared accounts.json.
/// Passwords are never stored in plaintext — only <see cref="PasswordHash"/>
/// (PBKDF2) and <see cref="Salt"/> (base64).
/// </summary>
public sealed record UserAccount
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("password_hash")]
    public string PasswordHash { get; init; } = string.Empty;

    [JsonPropertyName("salt")]
    public string Salt { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; init; } = UserRole.User;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "active"; // "active" | "disabled"

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; init; } = "system";

    [JsonIgnore]
    public bool IsActive => Status == "active";
}
