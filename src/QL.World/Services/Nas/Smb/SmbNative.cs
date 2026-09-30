using System.Runtime.InteropServices;

namespace QL.World.Services.Nas.Smb;

/// <summary>
/// Win32 <c>mpr.dll</c> P/Invoke signatures for SMB session lifecycle
/// (<see cref="WNetAddConnection2"/> / <see cref="WNetCancelConnection2"/>)
/// plus the Win32 error codes those APIs surface. <see cref="SmbNasConnector"/>
/// maps the codes to <see cref="NasConnectionKind"/>. Zero new NuGet — Windows
/// ships <c>mpr.dll</c> in every SKU since NT 3.1.
///
/// <see cref="WNetAddConnection2"/> is called with <see cref="CONNECT_TEMPORARY"/>
/// so credentials never persist to the user's Credential Manager; dispose
/// releases via <see cref="WNetCancelConnection2"/> with <c>fForce=true</c>
/// to avoid <see cref="ERROR_SESSION_CREDENTIAL_EXPIRED"/> (1219) on a
/// subsequent connect to the same host with different credentials.
/// </summary>
internal static class SmbNative
{
    // dwFlags for WNetAddConnection2 / WNetCancelConnection2.
    public const int CONNECT_UPDATE_PROFILE = 1;
    public const int CONNECT_TEMPORARY = 4;
    public const int CONNECT_INTERACTIVE = 8;

    // dwType for NETRESOURCE.
    public const int RESOURCETYPE_DISK = 1;

    // Win32 error codes returned by mpr.dll.
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PASSWORD = 86;
    public const int ERROR_BAD_NETPATH = 53;
    public const int ERROR_BUSY = 170;
    public const int ERROR_DEVICE_ALREADY_REMEMBERED = 1202;
    public const int ERROR_SESSION_CREDENTIAL_EXPIRED = 1219;
    public const int ERROR_NETWORK_UNREACHABLE = 1231;
    public const int ERROR_LOGON_FAILURE = 1326;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int WNetAddConnection2(
        ref NETRESOURCE lpNetResource,
        string? lpPassword,
        string? lpUsername,
        int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int WNetCancelConnection2(
        string lpName,
        int dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fForce);
}
