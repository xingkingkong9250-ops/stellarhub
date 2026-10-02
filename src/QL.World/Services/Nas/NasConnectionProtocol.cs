namespace QL.World.Services.Nas;

/// <summary>
/// NAS connection protocol driver. All three drivers are implemented:
/// <see cref="WebDav"/> (M5-B), <see cref="Smb"/> (M7-B) and
/// <see cref="Sftp"/> (M9). The enum values are persisted in
/// <see cref="NasConnectionSpec"/>; do not renumber.
/// </summary>
public enum NasConnectionProtocol
{
    /// <summary>WebDAV over HTTP(S) on top of <see cref="System.Net.Http.HttpClient"/>.</summary>
    WebDav = 0,

    /// <summary>SMB/CIFS via Win32 <c>mpr.dll</c> P/Invoke (zero new NuGet).</summary>
    Smb = 1,

    /// <summary>SFTP over SSH via SSH.NET; always requires a username.</summary>
    Sftp = 2,
}
