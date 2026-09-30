using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M7-A: smoke tests for <see cref="NasConnectorFactory"/>. Verifies the
/// dispatcher routes by <see cref="NasConnectionSpec.Protocol"/> to the
/// correct sub-factory, and that SMB/SFTP placeholders surface a clear
/// <see cref="NotSupportedException"/> naming the protocol so callers can
/// tell which driver is still pending. Real driver behavior is covered by
/// each driver's own contract tests (WebDav 14, SMB in M7-B).
/// </summary>
public sealed class NasConnectorFactoryTests
{
    [Fact]
    public void Create_routes_WebDav_to_WebDav_subfactory()
    {
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Smb),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Sftp));

        var spec = new NasConnectionSpec
        {
            Id = 1,
            Name = "wdav",
            Protocol = NasConnectionProtocol.WebDav,
            Host = "nas.local",
            RootPath = "/",
            Username = "u",
        };

        using INasConnector connector = factory.Create(spec, plaintextPassword: "p");
        Assert.NotNull(connector);
        Assert.Equal(NasConnectionProtocol.WebDav, connector.Spec.Protocol);
        Assert.Equal("nas.local", connector.Spec.Host);
    }

    [Theory]
    [InlineData(NasConnectionProtocol.Smb, "Smb")]
    [InlineData(NasConnectionProtocol.Sftp, "Sftp")]
    public void Create_throws_NotSupported_for_unimplemented_protocol(
        NasConnectionProtocol protocol, string protocolName)
    {
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Smb),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Sftp));

        var spec = new NasConnectionSpec
        {
            Id = 2,
            Name = "p",
            Protocol = protocol,
            Host = "nas.local",
            RootPath = "/",
            Username = "u",
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => factory.Create(spec, plaintextPassword: "p"));
        Assert.Contains(protocolName, ex.Message);
    }

    [Fact]
    public void Create_throws_ArgumentNull_for_null_spec()
    {
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Smb),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Sftp));

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!, plaintextPassword: null));
    }

    [Fact]
    public void Constructor_rejects_null_subfactories()
    {
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            null!, new UnsupportedProtocolFactory(NasConnectionProtocol.Smb),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Sftp)));
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            new WebDavConnectorFactory(), null!,
            new UnsupportedProtocolFactory(NasConnectionProtocol.Sftp)));
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new UnsupportedProtocolFactory(NasConnectionProtocol.Smb), null!));
    }

    [Fact]
    public void Constructor_rejects_WebDav_as_unsupported()
    {
        // WebDav has a real driver; passing it to UnsupportedProtocolFactory
        // would silently mask a wiring mistake. Constructor must catch it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnsupportedProtocolFactory(NasConnectionProtocol.WebDav));
    }
}
