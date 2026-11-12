using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Bloom.Notification.Email;

/// <summary>Opens a verification code sealed by Identity's <c>SecretBox</c> (NR-03): base64url of nonce(12) ‖ ciphertext ‖ tag(16), AES-256-GCM, no associated data.</summary>
public static class CodeCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <exception cref="FormatException">Not base64url.</exception>
    /// <exception cref="CryptographicException">Wrong key, tampered or truncated input.</exception>
    public static string Decrypt(byte[] key, string encryptedCode)
    {
        var sealedBytes = Base64Url.DecodeFromChars(encryptedCode);
        if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("Sealed code is too short.");
        var cipherLength = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[cipherLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize, cipherLength), sealedBytes.AsSpan(NonceSize + cipherLength), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
