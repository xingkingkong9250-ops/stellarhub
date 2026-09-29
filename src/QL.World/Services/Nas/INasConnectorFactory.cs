namespace QL.World.Services.Nas;

/// <summary>
/// Builds a configured <see cref="INasConnector"/> from a persisted
/// <see cref="NasConnectionSpec"/>. The factory is the single place that
/// knows how to turn a spec + password into a live connector (WebDAV today,
/// SMB/SFTP in later milestones). The returned connector is owned by the
/// caller and should be disposed when the owning scope ends.
/// </summary>
public interface INasConnectorFactory
{
    /// <summary>
    /// Creates a connector for <paramref name="spec"/>. When
    /// <paramref name="plaintextPassword"/> is null/empty, decrypts
    /// <see cref="NasConnectionSpec.EncryptedPassword"/> via DPAPI. The
    /// returned <see cref="INasConnector"/> is also
    /// <see cref="IDisposable"/> (dispose to release the underlying
    /// HttpClient / handler).
    /// </summary>
    INasConnector Create(NasConnectionSpec spec, string? plaintextPassword);
}
