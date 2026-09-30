using System.Security.Cryptography;

namespace QL.World.Helpers;

/// <summary>
/// M6-A: PBKDF2 password hashing (Rfc2898DeriveBytes). 16-byte salt,
/// 32-byte hash, 10000 iterations of HMAC-SHA256. Both salt and hash
/// are returned as base64 strings for JSON storage.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 10000;

    /// <summary>
    /// Hashes <paramref name="password"/> with a fresh random salt.
    /// Returns (base64Hash, base64Salt).
    /// </summary>
    public static (string Hash, string Salt) Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against the stored hash+salt.
    /// Returns true on match. Uses fixed-time comparison to avoid timing attacks.
    /// </summary>
    public static bool Verify(string password, string base64Hash, string base64Salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(base64Hash) || string.IsNullOrEmpty(base64Salt))
            return false;

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(base64Salt);
            expectedHash = Convert.FromBase64String(base64Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
