namespace QL.World.Helpers;

/// <summary>
/// Windows implementation of <see cref="IUserIdentity"/>. Reads
/// <see cref="System.Environment.UserName"/> and
/// <see cref="System.Environment.MachineName"/> once at construction; the
/// values are immutable for the process lifetime. macOS/Linux variants are
/// reserved for M6+.
/// </summary>
public sealed class UserIdentityService : IUserIdentity
{
    /// <inheritdoc />
    public string Username { get; }

    /// <inheritdoc />
    public string DeviceName { get; }

    public UserIdentityService()
    {
        Username = Environment.UserName;
        DeviceName = Environment.MachineName;
    }

    /// <inheritdoc />
    public string CombinedId => $"{Username}@{DeviceName}";
}
