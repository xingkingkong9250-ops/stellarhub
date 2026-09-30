namespace QL.World.Services.Nas;

/// <summary>
/// Placeholder <see cref="INasConnectorFactory"/> for a protocol driver that
/// has not landed yet (SMB lands in M7-B; SFTP is reserved for M8). Create
/// throws <see cref="NotSupportedException"/> with a message naming the
/// protocol so callers see a clear root-cause instead of a null-ref or
/// silent fallback. Returned by <see cref="NasConnectorFactory"/> for any
/// <see cref="NasConnectionProtocol"/> whose real driver is still pending.
/// </summary>
public sealed class UnsupportedProtocolFactory : INasConnectorFactory
{
    private readonly NasConnectionProtocol _protocol;

    public UnsupportedProtocolFactory(NasConnectionProtocol protocol)
    {
        if (protocol == NasConnectionProtocol.WebDav)
            throw new ArgumentOutOfRangeException(
                nameof(protocol),
                "WebDav has a real driver; do not mark it unsupported.");
        _protocol = protocol;
    }

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string milestone = _protocol switch
        {
            NasConnectionProtocol.Smb => "M7-B",
            NasConnectionProtocol.Sftp => "M8",
            _ => "a later milestone",
        };
        throw new NotSupportedException(
            $"{_protocol} driver lands in {milestone}; not implemented yet.");
    }
}
