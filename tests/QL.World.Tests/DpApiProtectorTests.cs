using QL.World.Interop;

namespace QL.World.Tests;

public sealed class DpApiProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips_plaintext()
    {
        const string secret = "sk-ql-world-test-key-12345";

        string? cipher = DpApiProtector.Protect(secret);
        Assert.NotNull(cipher);
        Assert.NotEqual(secret, cipher);

        string? decrypted = DpApiProtector.Unprotect(cipher);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Protect_returns_base64_ciphertext()
    {
        string? cipher = DpApiProtector.Protect("hello");
        Assert.NotNull(cipher);

        // Should decode as Base64.
        byte[] bytes = Convert.FromBase64String(cipher!);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Protect_null_or_empty_returns_null()
    {
        Assert.Null(DpApiProtector.Protect(null));
        Assert.Null(DpApiProtector.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_null_or_empty_returns_null()
    {
        Assert.Null(DpApiProtector.Unprotect(null));
        Assert.Null(DpApiProtector.Unprotect(string.Empty));
    }

    [Fact]
    public void Unprotect_tampered_ciphertext_returns_null()
    {
        string? cipher = DpApiProtector.Protect("secret");
        Assert.NotNull(cipher);

        // Flip the first character of the Base64 string.
        char[] chars = cipher!.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        string tampered = new(chars);

        Assert.Null(DpApiProtector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_invalid_base64_returns_null()
    {
        Assert.Null(DpApiProtector.Unprotect("!!!not-base64!!!"));
    }

    [Fact]
    public void Different_plaintexts_produce_different_ciphertexts()
    {
        string? a = DpApiProtector.Protect("alpha");
        string? b = DpApiProtector.Protect("beta");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }
}
