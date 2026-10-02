using Renci.SshNet;
using QL.World.Interop;
using QL.World.Services.Nas.Sftp;

namespace QL.World.Services.Nas;

/// <summary>
/// M9-B: SFTP implementation of <see cref="INasConnectorFactory"/>. Builds
/// an SSH.NET <see cref="ConnectionInfo"/> with password auth and the
/// <see cref="NasConnectionSpec.TrustSelfSigned"/> host-key policy, opens the
/// SSH+SFTP connection eagerly (fail fast, mirroring SMB's eager
/// <c>WNetAddConnection2</c>) and returns a connected
/// <see cref="SftpNasConnector"/>.
///
/// Credentials: SFTP always requires a username (anonymous SFTP is not
/// supported — unlike WebDAV ShareLink). Password comes from the
/// just-typed <c>plaintextPassword</c> when present, otherwise DPAPI
/// decrypts <see cref="NasConnectionSpec.EncryptedPassword"/>.
///
/// Host keys: <see cref="NasConnectionSpec.TrustSelfSigned"/> doubles as
/// "accept an unrecognized/self-signed SSH host key" (same field, no schema
/// change). When false and the server presents a key, the connection is
/// refused with a <see cref="NasConnectionKind.ProtocolError"/> carrying the
/// SHA256 fingerprint so the user can verify it out-of-band before trusting.
/// </summary>
public sealed class SftpConnectorFactory : INasConnectorFactory
{
    internal const int DefaultPort = 22;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Username))
        {
            throw new NasConnectionException(
                NasConnectionKind.ProtocolError,
                "SFTP requires a username; anonymous SFTP is not supported.");
        }

        string password = !string.IsNullOrEmpty(plaintextPassword)
            ? plaintextPassword
            : DpApiProtector.Unprotect(spec.EncryptedPassword) ?? string.Empty;

        int port = spec.Port ?? DefaultPort;
        ConnectionInfo info = BuildConnectionInfo(spec, port, password);
        var client = new SftpClient(info);

        string? seenFingerprint = null;
        string? seenKeyName = null;
        client.HostKeyReceived += (_, args) =>
        {
            seenFingerprint = args.FingerPrintSHA256;
            seenKeyName = args.HostKeyName;
            args.CanTrust = SftpHostKeyPolicy.Decide(spec.TrustSelfSigned);
        };

        var session = new RenciSftpSession(client);
        try
        {
            // Sync factory boundary over an async connect: SSH.NET completes
            // the handshake on IO threads with no captured SynchronizationContext,
            // so GetAwaiter().GetResult() cannot deadlock the UI thread here.
            session.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (NasConnectionException ex) when (
            !spec.TrustSelfSigned
            && seenFingerprint is not null
            && ex.Kind != NasConnectionKind.AuthenticationFailed
            && ex.Kind != NasConnectionKind.Timeout)
        {
            // Key exchange happened (we saw a fingerprint) and trust was
            // withheld — the failure is the host-key policy, not the network.
            // Surface the fingerprint so it can be verified out-of-band.
            throw new NasConnectionException(
                NasConnectionKind.ProtocolError,
                $"SSH host key was not trusted (key: {seenKeyName ?? "unknown"}, " +
                $"fingerprint {SftpHostKeyPolicy.FormatFingerprint(seenFingerprint)}). " +
                "Verify the fingerprint with your NAS administrator, then enable " +
                "'trust self-signed certificates' for this connection to accept it.",
                ex);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return new SftpNasConnector(spec, session);
    }

    /// <summary>
    /// Pure construction of the SSH.NET <see cref="ConnectionInfo"/> (password
    /// auth only; keyboard-interactive/public-key are not offered by StellarHub).
    /// The host-key handler is attached in <see cref="Create"/> because it must
    /// observe the runtime trust decision; this method stays unit-testable.
    /// </summary>
    internal static ConnectionInfo BuildConnectionInfo(NasConnectionSpec spec, int port, string password)
    {
        var auth = new PasswordAuthenticationMethod(spec.Username, password);
        var info = new ConnectionInfo(spec.Host, port, spec.Username, auth)
        {
            Timeout = ConnectTimeout,
        };
        return info;
    }
}

/// <summary>
/// Pure host-key policy helpers, split out so the trust decision and
/// fingerprint formatting are trivially testable without an SSH server.
/// </summary>
internal static class SftpHostKeyPolicy
{
    /// <summary>Trust the presented key only when the user opted in per-connection.</summary>
    internal static bool Decide(bool trustSelfSigned) => trustSelfSigned;

    /// <summary>
    /// SSH.NET reports <c>FingerPrintSHA256</c> without the standard
    /// <c>SHA256:</c> prefix (OpenSSH known-hosts style); add it for display.
    /// </summary>
    internal static string FormatFingerprint(string rawSha256)
        => rawSha256.StartsWith("SHA256:", StringComparison.Ordinal)
            ? rawSha256
            : "SHA256:" + rawSha256;
}
