using System.Security.Cryptography;
using VoiceCraft.Core.Security;

namespace VoiceCraft.Tests;

public class NetworkSecurityTests
{
    private static void CompleteHandshake(NetworkSecurity a, NetworkSecurity b)
    {
        a.CompleteHandshake(b.PublicKey);
        b.CompleteHandshake(a.PublicKey);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ShouldMatch()
    {
        using var a = new NetworkSecurity();
        using var b = new NetworkSecurity();
        CompleteHandshake(a, b);

        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        var (ciphertext, iv, tag) = a.Encrypt(plaintext);
        var decrypted = b.Decrypt(ciphertext, iv, tag);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ShouldGenerateUniqueNonces()
    {
        using var a = new NetworkSecurity();
        using var b = new NetworkSecurity();
        CompleteHandshake(a, b);

        var seen = new HashSet<string>();
        for (var i = 0; i < 256; i++)
        {
            var (_, iv, _) = a.Encrypt(new byte[] { 7 });
            Assert.True(seen.Add(Convert.ToHexString(iv)));
        }
    }

    [Fact]
    public void Decrypt_ShouldRejectReplays()
    {
        using var a = new NetworkSecurity();
        using var b = new NetworkSecurity();
        CompleteHandshake(a, b);

        var payload = new byte[] { 9, 8, 7 };
        var (ciphertext, iv, tag) = a.Encrypt(payload);

        var decrypted = b.Decrypt(ciphertext, iv, tag);
        Assert.Equal(payload, decrypted);

        Assert.Throws<CryptographicException>(() => b.Decrypt(ciphertext, iv, tag));
    }

    [Fact]
    public void Decrypt_ShouldAcceptOutOfOrderWithinWindow()
    {
        using var a = new NetworkSecurity();
        using var b = new NetworkSecurity();
        CompleteHandshake(a, b);

        var (c1, iv1, t1) = a.Encrypt(new byte[] { 1 });
        var (c2, iv2, t2) = a.Encrypt(new byte[] { 2 });
        var (c3, iv3, t3) = a.Encrypt(new byte[] { 3 });

        Assert.Equal(new byte[] { 1 }, b.Decrypt(c1, iv1, t1));
        Assert.Equal(new byte[] { 3 }, b.Decrypt(c3, iv3, t3));
        Assert.Equal(new byte[] { 2 }, b.Decrypt(c2, iv2, t2));
    }
}
