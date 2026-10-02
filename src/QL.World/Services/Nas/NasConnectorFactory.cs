namespace QL.World.Services.Nas;

/// <summary>
/// Multi-protocol <see cref="INasConnectorFactory"/>. Routes
/// <see cref="INasConnectorFactory.Create"/> to a per-protocol sub-factory
/// based on <see cref="NasConnectionSpec.Protocol"/>. Sub-factories are
/// injected at construction so each driver owns its own concerns (WebDAV
/// <c>HttpClient</c> setup, SMB <c>WNetAddConnection2</c> P/Invoke, SFTP
/// SSH.NET eager connect + host-key policy). The dispatcher itself stays
/// stateless beyond holding the three sub-factories.
///
/// As of M9 all three sub-factories are real implementations;
/// <see cref="UnsupportedProtocolFactory"/> remains available for any
/// future protocol added before its driver lands.
/// </summary>
public sealed class NasConnectorFactory : INasConnectorFactory
{
    private readonly INasConnectorFactory _webDavFactory;
    private readonly INasConnectorFactory _smbFactory;
    private readonly INasConnectorFactory _sftpFactory;

    public NasConnectorFactory(
        INasConnectorFactory webDavFactory,
        INasConnectorFactory smbFactory,
        INasConnectorFactory sftpFactory)
    {
        _webDavFactory = webDavFactory ?? throw new ArgumentNullException(nameof(webDavFactory));
        _smbFactory = smbFactory ?? throw new ArgumentNullException(nameof(smbFactory));
        _sftpFactory = sftpFactory ?? throw new ArgumentNullException(nameof(sftpFactory));
    }

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Protocol switch
        {
            NasConnectionProtocol.WebDav => _webDavFactory.Create(spec, plaintextPassword),
            NasConnectionProtocol.Smb => _smbFactory.Create(spec, plaintextPassword),
            NasConnectionProtocol.Sftp => _sftpFactory.Create(spec, plaintextPassword),
            _ => throw new ArgumentOutOfRangeException(
                nameof(spec),
                spec.Protocol,
                "unknown NAS protocol"),
        };
    }
}
