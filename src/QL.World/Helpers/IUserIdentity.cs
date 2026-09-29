namespace QL.World.Helpers;

/// <summary>
/// Identifies the current OS user. M5 uses it for log lines and per-user
/// settings partitioning; M6+ will use it for permission / role checks.
/// The Windows implementation lives in <see cref="UserIdentityService"/>;
/// macOS/Linux variants (NSUserName / SCDynamicStoreCopyComputerName) are
/// reserved for M6+.
/// </summary>
public interface IUserIdentity
{
    /// <summary>Logged-in OS user name (e.g. <c>alice</c>). Never null/empty on Windows.</summary>
    string Username { get; }

    /// <summary>Machine / host name (e.g. <c>DESKTOP-ABC</c>). Never null/empty on Windows.</summary>
    string DeviceName { get; }

    /// <summary>Stable combined identifier <c>"username@devicename"</c>.</summary>
    string CombinedId { get; }
}
