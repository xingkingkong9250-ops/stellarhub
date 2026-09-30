using QL.World.Helpers;
using Xunit;

namespace QL.World.Tests;

/// <summary>
/// M6-A: PBKDF2 hashing — same password hashes differently (random salt),
/// verify returns true on match, false on mismatch, null/empty inputs safe.
/// </summary>
public class PasswordHasherTests
{
    [Fact]
    public void Hash_produces_nonempty_base64_hash_and_salt()
    {
        var (hash, salt) = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(string.IsNullOrEmpty(hash));
        Assert.False(string.IsNullOrEmpty(salt));
        Assert.NotEqual(hash, salt);
    }

    [Fact]
    public void Hash_same_password_yields_different_salts()
    {
        var (h1, s1) = PasswordHasher.Hash("secret123");
        var (h2, s2) = PasswordHasher.Hash("secret123");

        Assert.NotEqual(s1, s2);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Verify_returns_true_for_correct_password()
    {
        var (hash, salt) = PasswordHasher.Hash("mypassword");

        Assert.True(PasswordHasher.Verify("mypassword", hash, salt));
    }

    [Fact]
    public void Verify_returns_false_for_wrong_password()
    {
        var (hash, salt) = PasswordHasher.Hash("correct");

        Assert.False(PasswordHasher.Verify("wrong", hash, salt));
    }

    [Fact]
    public void Verify_returns_false_for_empty_hash_or_salt()
    {
        Assert.False(PasswordHasher.Verify("pw", "", "salt"));
        Assert.False(PasswordHasher.Verify("pw", "hash", ""));
    }

    [Fact]
    public void Verify_returns_false_for_malformed_base64()
    {
        Assert.False(PasswordHasher.Verify("pw", "!!!not-base64!!!", "!!!not-base64!!!"));
    }
}
