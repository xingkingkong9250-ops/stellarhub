using QL.World.Interop;
using QL.World.Services.Nas.Smb;

namespace QL.World.Services.Nas;

/// <summary>
/// SMB implementation of <see cref="INasConnectorFactory"/>. Splits
/// <see cref="NasConnectionSpec.RootPath"/> into (shareName, subPath),
/// authenticates against <c>\\host\share</c> via
/// <see cref="SmbNative.WNetAddConnection2"/> with CONNECT_TEMPORARY
/// (credentials never persist to the Credential Manager), and returns a
/// <see cref="SmbNasConnector"/> whose <c>uncRoot</c> already includes the
/// subPath. Zero new NuGet.
///
/// <see cref="NasConnectionSpec.UseTls"/> is informational here — SMB 3.1.1
/// negotiates encryption automatically when both ends support it; mpr.dll
/// exposes no separate toggle, so the flag does not change the call. The
/// UI surfaces a per-protocol label so users see the equivalence.
/// </summary>
public sealed class SmbConnectorFactory : INasConnectorFactory
{
    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // RootPath is forward-slash relative ("StellarHubShared/" or
        // "StellarHubShared/sub"). Split into share + sub so we can pass the
        // bare \\host\share to WNetAddConnection2 and bake the sub into the
        // connector's uncRoot.
        string root = (spec.RootPath ?? "/").Trim().Trim('/', '\\');
        string[] parts = root.Split(new[] { '/', '\\' }, 2, StringSplitOptions.None);
        string shareName = parts[0];
        string? subPath = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
        if (string.IsNullOrEmpty(shareName))
            throw new NasConnectionException(
                NasConnectionKind.ProtocolError,
                $"SMB RootPath must include share name; got '{spec.RootPath}'");

        string remoteName = $@"\\{spec.Host}\{shareName}";
        string uncRoot = subPath is null ? remoteName : $@"{remoteName}\{subPath}";

        string? password = !string.IsNullOrEmpty(plaintextPassword)
            ? plaintextPassword
            : DpApiProtector.Unprotect(spec.EncryptedPassword);
        string? username = !string.IsNullOrEmpty(spec.Username) ? spec.Username : null;

        bool ownsConnection = false;
        if (username is not null)
        {
            var nr = new SmbNative.NETRESOURCE
            {
                dwType = SmbNative.RESOURCETYPE_DISK,
                lpLocalName = null,
                lpRemoteName = remoteName,
                lpComment = null,
                lpProvider = null,
            };
            int err = SmbNative.WNetAddConnection2(
                ref nr, password, username, SmbNative.CONNECT_TEMPORARY);
            if (err != SmbNative.ERROR_SUCCESS)
                throw MapWin32(err, remoteName);
            ownsConnection = true;
        }
        // username null → no WNet call; rely on the caller's Windows session
        // for implicit authentication against the share.

        return new SmbNasConnector(spec, uncRoot, ownsConnection ? remoteName : null, ownsConnection);
    }

    private static NasConnectionException MapWin32(int err, string remoteName)
    {
        NasConnectionKind kind = err switch
        {
            SmbNative.ERROR_LOGON_FAILURE or
            SmbNative.ERROR_INVALID_PASSWORD or
            SmbNative.ERROR_ACCESS_DENIED or
            SmbNative.ERROR_SESSION_CREDENTIAL_EXPIRED =>
                NasConnectionKind.AuthenticationFailed,
            SmbNative.ERROR_BAD_NETPATH or
            SmbNative.ERROR_NETWORK_UNREACHABLE =>
                NasConnectionKind.NetworkUnreachable,
            _ => NasConnectionKind.ProtocolError,
        };
        return new NasConnectionException(
            kind, $"WNetAddConnection2 to {remoteName} failed (Win32={err})", inner: null);
    }
}
