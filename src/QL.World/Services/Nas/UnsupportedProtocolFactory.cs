namespace QL.World.Services.Nas;

/// <summary>
/// Placeholder <see cref="INasConnectorFactory"/> for a protocol driver that
/// has not landed yet. Create throws <see cref="NotSupportedException"/>
/// with a message naming the protocol so callers see a clear root-cause
/// instead of a null-ref or silent fallback. As of M9 all shipped protocols
/// (WebDAV/SMB/SFTP) have real drivers; this remains for any future protocol
/// enum value wired into <see cref="NasConnectorFactory"/> before its
/// driver is implemented.
/// </summary>
public sealed class UnsupportedProtocolFactory : INasConnectorFactory
{
    private readonly NasConnectionProtocol _protocol;

    public UnsupportedProtocolFactory(NasConnectionProtocol protocol)
    {
        if (protocol is NasConnectionProtocol.WebDav
            or NasConnectionProtocol.Smb
            or NasConnectionProtocol.Sftp)
            throw new ArgumentOutOfRangeException(
                nameof(protocol),
                $"{protocol} has a real driver; do not mark it unsupported.");
        _protocol = protocol;
    }

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);
        throw new NotSupportedException(
            $"{_protocol} driver is not implemented yet.");
    }
}
