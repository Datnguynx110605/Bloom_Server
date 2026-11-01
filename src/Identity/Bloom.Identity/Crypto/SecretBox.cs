using System.Security.Cryptography;

namespace Bloom.Identity.Crypto;

/// <summary>AES-256-GCM, no associated data: nonce(12) ‖ ciphertext ‖ tag(16) (spec §7).</summary>
public sealed class SecretBox(byte[] key)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes = new(key, TagSize);

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var result = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, plaintext, result.AsSpan(NonceSize, plaintext.Length), result.AsSpan(NonceSize + plaintext.Length));
        return result;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered or truncated input.</exception>
    public byte[] Open(ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("Sealed value is too short.");
        var cipherLength = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[cipherLength];
        _aes.Decrypt(sealedBytes[..NonceSize], sealedBytes.Slice(NonceSize, cipherLength), sealedBytes[(NonceSize + cipherLength)..], plaintext);
        return plaintext;
    }
}
