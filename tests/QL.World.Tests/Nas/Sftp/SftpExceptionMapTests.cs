using System.Net.Sockets;
using QL.World.Services.Nas;
using QL.World.Services.Nas.Sftp;
using Renci.SshNet.Common;

namespace QL.World.Tests.Nas.Sftp;

/// <summary>
/// M9-A: verifies <see cref="SftpExceptionMap"/> classifies every relevant
/// SSH.NET/socket exception into the project's <see cref="NasConnectionKind"/>
/// taxonomy. Fabricated exception instances — no live server.
/// </summary>
public sealed class SftpExceptionMapTests
{
    [Fact]
    public void SshAuthenticationException_maps_to_AuthenticationFailed()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SshAuthenticationException("bad creds")));
        Assert.Equal(NasConnectionKind.AuthenticationFailed, mapped.Kind);
    }

    [Fact]
    public void SftpPermissionDeniedException_maps_to_AuthenticationFailed()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SftpPermissionDeniedException("denied")));
        Assert.Equal(NasConnectionKind.AuthenticationFailed, mapped.Kind);
    }

    [Fact]
    public void SshOperationTimeoutException_maps_to_Timeout()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SshOperationTimeoutException("slow")));
        Assert.Equal(NasConnectionKind.Timeout, mapped.Kind);
    }

    [Fact]
    public void Socket_timeout_maps_to_Timeout()
    {
        // 10060 = WSAETIMEDOUT -> SocketError.TimedOut
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SocketException(10060)));
        Assert.Equal(NasConnectionKind.Timeout, mapped.Kind);
    }

    [Fact]
    public void Socket_refused_maps_to_NetworkUnreachable()
    {
        // 10061 = WSAECONNREFUSED
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SocketException(10061)));
        Assert.Equal(NasConnectionKind.NetworkUnreachable, mapped.Kind);
    }

    [Fact]
    public void SftpPathNotFoundException_maps_to_NotFound()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SftpPathNotFoundException("no such path")));
        Assert.Equal(NasConnectionKind.NotFound, mapped.Kind);
    }

    [Fact]
    public void SshConnectionException_maps_to_NetworkUnreachable()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SshConnectionException("reset by peer")));
        Assert.Equal(NasConnectionKind.NetworkUnreachable, mapped.Kind);
    }

    [Fact]
    public void SshException_maps_to_ProtocolError()
    {
        // SftpException derives from SshException and is covered by the same case.
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new SshException("ssh failure")));
        Assert.Equal(NasConnectionKind.ProtocolError, mapped.Kind);
    }

    [Fact]
    public void Unknown_exception_maps_to_Unknown()
    {
        var mapped = Assert.IsType<NasConnectionException>(
            SftpExceptionMap.Map(new InvalidOperationException("surprise")));
        Assert.Equal(NasConnectionKind.Unknown, mapped.Kind);
    }

    [Fact]
    public void NasConnectionException_passes_through_unchanged()
    {
        var original = new NasConnectionException(NasConnectionKind.NotFound, "keep");
        Assert.Same(original, SftpExceptionMap.Map(original));
    }

    [Fact]
    public void OperationCanceledException_passes_through_unchanged()
    {
        var original = new OperationCanceledException();
        Assert.Same(original, SftpExceptionMap.Map(original));
    }

    [Fact]
    public void Mapped_exception_preserves_inner_exception()
    {
        var inner = new SshAuthenticationException("root cause");
        var mapped = Assert.IsType<NasConnectionException>(SftpExceptionMap.Map(inner));
        Assert.Same(inner, mapped.InnerException);
    }
}
