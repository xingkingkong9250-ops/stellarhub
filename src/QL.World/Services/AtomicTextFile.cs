using System.IO;
using System.Text;

namespace QL.World.Services;

/// <summary>
/// Commits small UTF-8 state files through a same-directory atomic replace.
/// The previous complete file is retained as a last-known-good snapshot.
/// Ported verbatim in behavior from QianLan.YinWu.
/// </summary>
internal static class AtomicTextFile
{
    internal const string BackupSuffix = ".backup";

    public static string GetBackupPath(string path) => path + BackupSuffix;

    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A state file must have a parent directory.", nameof(path));

        Directory.CreateDirectory(directory);
        string temporaryPath = CreateTemporaryPath(path);
        bool committed = false;
        try
        {
            WriteDurableUtf8(temporaryPath, content);
            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    GetBackupPath(path),
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path, overwrite: false);
            }

            committed = true;
        }
        finally
        {
            TryDeleteOwnedTemporary(temporaryPath);
        }

        if (committed)
            TryCreateInitialBackup(path);
    }

    public static void RestoreBackup(string path)
    {
        string backupPath = GetBackupPath(path);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Recovery snapshot was not found.", backupPath);

        string temporaryPath = CreateTemporaryPath(path);
        try
        {
            CopyDurably(backupPath, temporaryPath);
            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
        }
        finally
        {
            TryDeleteOwnedTemporary(temporaryPath);
        }
    }

    private static void TryCreateInitialBackup(string path)
    {
        string backupPath = GetBackupPath(path);
        if (File.Exists(backupPath)) return;

        string temporaryPath = CreateTemporaryPath(backupPath);
        try
        {
            CopyDurably(path, temporaryPath);
            File.Move(temporaryPath, backupPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Another successful writer created the same recovery snapshot.
        }
        catch (Exception ex)
        {
            // The primary file is already committed. Backup creation is a
            // recoverability warning, not a reason to report the save as lost.
            System.Diagnostics.Debug.WriteLine(
                $"[AtomicTextFile] Initial backup failed: {ex.Message}");
        }
        finally
        {
            TryDeleteOwnedTemporary(temporaryPath);
        }
    }

    private static void WriteDurableUtf8(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void CopyDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static string CreateTemporaryPath(string path) =>
        path + $".{Guid.NewGuid():N}.partial";

    private static void TryDeleteOwnedTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AtomicTextFile] Temporary cleanup failed: {ex.Message}");
        }
    }
}
