using QL.World.Services.Nas;
using QL.World.Services.Nas.Sftp;

namespace QL.World.Tests.Nas.Sftp;

/// <summary>
/// M9-B: <see cref="SftpConnectorFactory"/> — credential/policy validation
/// and <see cref="SftpHostKeyPolicy"/> pure functions are fully testable
/// offline; one refused-port case exercises the eager-connect failure path
/// end to end through <see cref="SftpExceptionMap"/>.
/// </summary>
public sealed class SftpConnectorFactoryTests
{
    private static NasConnectionSpec SftpSpec(string host = "nas.local", int? port = null) => new()
    {
        Id = 1,
        Name = "sftp",
        Protocol = NasConnectionProtocol.Sftp,
        Host = host,
        Port = port,
        RootPath = "/",
        Username = "alice",
    };

    [Fact]
    public void Create_throws_ProtocolError_when_username_missing()
    {
        var factory = new SftpConnectorFactory();
        var spec = SftpSpec();
        spec.Username = "   ";

        var ex = Assert.Throws<NasConnectionException>(
            () => factory.Create(spec, plaintextPassword: "pw"));
        Assert.Equal(NasConnectionKind.ProtocolError, ex.Kind);
    }

    [Fact]
    public void Create_throws_ArgumentNull_for_null_spec()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SftpConnectorFactory().Create(null!, plaintextPassword: null));
    }

    [Fact]
    public void BuildConnectionInfo_carries_host_port_username_and_30s_timeout()
    {
        var spec = SftpSpec(host: "ugreen.local", port: 2222);

        Renci.SshNet.ConnectionInfo info =
            SftpConnectorFactory.BuildConnectionInfo(spec, port: 2222, password: "secret");

        Assert.Equal("ugreen.local", info.Host);
        Assert.Equal(2222, info.Port);
        Assert.Equal("alice", info.Username);
        Assert.Equal(TimeSpan.FromSeconds(30), info.Timeout);
    }

    [Fact]
    public void Create_to_refused_port_throws_classified_NasConnectionException()
    {
        // 127.0.0.1:1 is refused immediately on Windows (TCP RST) — exercises
        // the real SftpClient eager-connect + exception map without a server.
        var factory = new SftpConnectorFactory();
        var spec = SftpSpec(host: "127.0.0.1", port: 1);

        var ex = Assert.Throws<NasConnectionException>(
            () => factory.Create(spec, plaintextPassword: "pw"));
        Assert.Contains(
            ex.Kind,
            new[] { NasConnectionKind.NetworkUnreachable, NasConnectionKind.Timeout });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HostKeyPolicy_decide_follows_opt_in(bool trustSelfSigned, bool expectedCanTrust)
    {
        Assert.Equal(expectedCanTrust, SftpHostKeyPolicy.Decide(trustSelfSigned));
    }

    [Theory]
    [InlineData("abc123", "SHA256:abc123")]
    [InlineData("SHA256:already", "SHA256:already")]
    public void HostKeyPolicy_format_fingerprint_adds_prefix_once(string raw, string expected)
    {
        Assert.Equal(expected, SftpHostKeyPolicy.FormatFingerprint(raw));
    }
}
