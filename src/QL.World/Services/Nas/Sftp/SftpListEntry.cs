namespace QL.World.Services.Nas.Sftp;

/// <summary>
/// M9-A: protocol-neutral listing entry produced by <see cref="ISftpSession"/>.
/// Mirrors the subset of <c>Renci.SshNet.Sftp.ISftpFile</c> the connector
/// needs, so <see cref="SftpNasConnector"/> never references Renci types
/// directly (the fake test session implements the same shape over a local
/// directory).
/// </summary>
internal sealed record SftpListEntry(
    string Name,
    string FullName,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc);
