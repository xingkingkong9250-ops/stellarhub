using System.Text.Json.Serialization;

namespace QL.World.Services.Nas;

/// <summary>
/// Persisted NAS connection definition stored inside the AppSettings
/// <c>NasConnections</c> list (M5-C). The plaintext password is NEVER stored
/// here — only <see cref="EncryptedPassword"/>, encrypted via DPAPI at
/// CurrentUser scope (same pattern as the cloud AI API key).
/// <see cref="Protocol"/> is forward-compatible with SMB/SFTP so a persisted
/// connection keeps working when those drivers ship in later milestones.
/// </summary>
public sealed class NasConnectionSpec
{
    /// <summary>Stable numeric id; primary key in the <c>nas_connections</c> SQLite table (M5-D).</summary>
    public int Id { get; set; }

    /// <summary>User-facing display name; unique across all connections.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Protocol driver. M5 honors <see cref="NasConnectionProtocol.WebDav"/> only.</summary>
    public NasConnectionProtocol Protocol { get; set; } = NasConnectionProtocol.WebDav;

    /// <summary>Host name or IP literal (no scheme/port).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Null/0 means the protocol default (WebDAV: 443 TLS, 80 plain).</summary>
    public int? Port { get; set; }

    /// <summary>Absolute path on the NAS used as the listing root (e.g. <c>/photos/</c>).</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>Username for Basic/Digest auth; empty for anonymous.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted password (CurrentUser scope); null when not set.</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>
    /// Use HTTPS (true, default) or plain HTTP (false). Plain HTTP is allowed
    /// for trusted LAN-only NAS but emits a warning in the UI.
    /// 默认 true 的 bool 必须 Never 忽略：WhenWritingDefault 会吞掉 true。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool UseTls { get; set; } = true;

    /// <summary>Trust self-signed TLS certs (M5-B). Defaults to false for safety.</summary>
    public bool TrustSelfSigned { get; set; } = false;

    /// <summary>
    /// M8-A: NAS 角色（Primary/Secondary/ShareLink）。默认 Secondary 与 v0.2.1
    /// IsPrimary 默认 false 保持语义一致。<see cref="IsPrimary"/> 是其计算属性，
    /// 保留 setter 用于旧 config 反序列化兼容（IsPrimary=true → Role=Primary）。
    /// <see cref="AppSettingsService.ValidateNasRoles"/> 保证全列表至多 1 个 Primary。
    /// </summary>
    public NasRole Role { get; set; } = NasRole.Secondary;

    /// <summary>
    /// v0.2.1: 管理员标记的主 NAS（计算属性）。
    /// M8-A: 升级为 <see cref="Role"/>==<see cref="NasRole.Primary"/> 的语义；
    /// 此处保留为计算属性 + 副作用 setter，向后兼容旧 config.json 反序列化
    /// （旧 JSON 含 isPrimary=true 字段时 setter 会把 Role 设为 Primary）。
    /// 新代码应直接读写 <see cref="Role"/> 字段。
    /// </summary>
    public bool IsPrimary
    {
        get => Role == NasRole.Primary;
        set => Role = value ? NasRole.Primary : NasRole.Secondary;
    }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
