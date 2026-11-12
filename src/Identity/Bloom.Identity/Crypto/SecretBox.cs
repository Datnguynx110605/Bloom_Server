using System.Security.Cryptography;

namespace Bloom.Identity.Crypto;

/// <summary>AES-256-GCM, no associated data: nonce(12) ‖ ciphertext ‖ tag(16) (spec §7).</summary>
public sealed class SecretBox(byte[] key)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        using var aes = new AesGcm(key, TagSize); // per call: AesGcm instances are not documented as thread-safe
        var result = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, plaintext, result.AsSpan(NonceSize, plaintext.Length), result.AsSpan(NonceSize + plaintext.Length));
        return result;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered or truncated input.</exception>
    public byte[] Open(ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("Sealed value is too short.");
        using var aes = new AesGcm(key, TagSize);
        var cipherLength = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[cipherLength];
        aes.Decrypt(sealedBytes[..NonceSize], sealedBytes.Slice(NonceSize, cipherLength), sealedBytes[(NonceSize + cipherLength)..], plaintext);
        return plaintext;
    }
}
