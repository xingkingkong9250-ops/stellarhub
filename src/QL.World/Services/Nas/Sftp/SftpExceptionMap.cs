using System.Net.Sockets;
using Renci.SshNet.Common;

namespace QL.World.Services.Nas.Sftp;

/// <summary>
/// M9-A: single place that classifies SSH.NET / socket exceptions into the
/// project's <see cref="NasConnectionKind"/> taxonomy. Kept static and
/// dependency-light so it can be unit-tested directly with fabricated
/// exception instances (no live SFTP server needed).
/// </summary>
internal static class SftpExceptionMap
{
    /// <summary>
    /// Maps a transport exception. <see cref="OperationCanceledException"/>
    /// (cooperative cancellation) and <see cref="NasConnectionException"/>
    /// pass through unchanged; everything else becomes a
    /// <see cref="NasConnectionException"/> carrying the original as inner.
    /// </summary>
    public static Exception Map(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
            case NasConnectionException:
                return ex;

            case SshAuthenticationException:
                return new NasConnectionException(
                    NasConnectionKind.AuthenticationFailed,
                    $"sftp authentication failed: {ex.Message}", ex);

            case SftpPermissionDeniedException:
                return new NasConnectionException(
                    NasConnectionKind.AuthenticationFailed,
                    $"sftp permission denied: {ex.Message}", ex);

            case SshOperationTimeoutException:
                return new NasConnectionException(
                    NasConnectionKind.Timeout,
                    $"sftp operation timed out: {ex.Message}", ex);

            case SftpPathNotFoundException:
                return new NasConnectionException(
                    NasConnectionKind.NotFound,
                    $"sftp path not found: {ex.Message}", ex);

            case SocketException sx:
                return new NasConnectionException(
                    sx.SocketErrorCode == SocketError.TimedOut
                        ? NasConnectionKind.Timeout
                        : NasConnectionKind.NetworkUnreachable,
                    $"sftp socket error ({sx.SocketErrorCode}): {sx.Message}", sx);

            case SshConnectionException:
                // Includes host-key rejection surfaced by SSH.NET after the
                // HostKeyReceived handler sets CanTrust=false; the factory
                // augments the message with the fingerprint before throwing.
                return new NasConnectionException(
                    NasConnectionKind.NetworkUnreachable,
                    $"sftp connection error: {ex.Message}", ex);

            // SftpException derives from SshException, so a single case
            // covers both (SFTP status failures not already specialized above).
            case SshException:
                return new NasConnectionException(
                    NasConnectionKind.ProtocolError,
                    $"sftp protocol error: {ex.Message}", ex);

            default:
                return new NasConnectionException(
                    NasConnectionKind.Unknown,
                    $"sftp unexpected error: {ex.Message}", ex);
        }
    }

    /// <summary>Convenience wrapper: rethrows the (possibly mapped) exception.</summary>
    public static Exception ToThrow(Exception ex) => Map(ex);
}
