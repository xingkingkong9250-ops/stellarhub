using System.Runtime.InteropServices;
using System.Text;

namespace QL.World.Interop;

/// <summary>
/// Thin wrapper over Windows DPAPI (CryptProtectData) using the
/// CurrentUser scope. Ciphertext is returned as a Base64 string so
/// it can be stored in config.json. Failures return null rather than
/// throwing — callers are expected to treat null as "could not protect".
/// </summary>
internal static class DpApiProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        StringBuilder? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    /// <summary>
    /// Encrypts plaintext with DPAPI (CurrentUser) and returns Base64.
    /// Returns null on failure.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        var input = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            var entropy = new DATA_BLOB(); // no optional entropy
            if (!CryptProtectData(ref input, "QL.World.ApiKey", ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB output))
                return null;

            try
            {
                byte[] cipher = new byte[output.cbData];
                Marshal.Copy(output.pbData, cipher, 0, output.cbData);
                return Convert.ToBase64String(cipher);
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(input.pbData);
        }
    }

    /// <summary>
    /// Decrypts a Base64 DPAPI ciphertext back to plaintext.
    /// Returns null on failure (including tampered input).
    /// </summary>
    public static string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return null;

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(cipherText);
        }
        catch
        {
            return null;
        }

        var input = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
            var entropy = new DATA_BLOB();
            if (!CryptUnprotectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB output))
                return null;

            try
            {
                byte[] plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(input.pbData);
        }
    }
}
