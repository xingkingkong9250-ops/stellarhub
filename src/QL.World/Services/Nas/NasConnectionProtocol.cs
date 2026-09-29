namespace QL.World.Services.Nas;

/// <summary>
/// NAS connection protocol driver. M5 ships only <see cref="WebDav"/>;
/// <see cref="Smb"/> and <see cref="Sftp"/> are reserved for later milestones
/// so persisted <see cref="NasConnectionSpec"/> entries stay forward-compatible.
/// </summary>
public enum NasConnectionProtocol
{
    /// <summary>WebDAV over HTTP(S) — implemented in M5-B on top of <see cref="System.Net.Http.HttpClient"/>.</summary>
    WebDav = 0,

    /// <summary>SMB/CIFS via Win32 <c>mpr.dll</c> P/Invoke — reserved for a later milestone (zero new NuGet).</summary>
    Smb = 1,

    /// <summary>SFTP via SSH.NET — reserved for a later milestone.</summary>
    Sftp = 2,
}
