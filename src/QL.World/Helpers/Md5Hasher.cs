using System.IO;
using System.Security.Cryptography;

namespace QL.World.Helpers;

/// <summary>Stable content identity used for dedupe and thumbnail paths.</summary>
public static class Md5Hasher
{
    private const int STREAM_BUFFER_SIZE = 1024 * 1024;

    public static string ComputeFile(string path)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: STREAM_BUFFER_SIZE, FileOptions.SequentialScan);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }
}
