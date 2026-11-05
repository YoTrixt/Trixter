
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace WpfApp1.PasswordManager;

public static class VaultCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int KeySize = 32;
    private const int TagSize = 16;

    public static byte[] CreateSalt()
    {
        return RandomNumberGenerator.GetBytes(SaltSize);
    }

    public static byte[] DeriveKey(string masterPassword, byte[] salt)
    {
        var argon2 = new Argon2id(
            System.Text.Encoding.UTF8.GetBytes(masterPassword))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            Iterations = 3,
            MemorySize = 65536
        };

        return argon2.GetBytes(KeySize);
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return nonce.Concat(tag).Concat(ciphertext).ToArray();
    }

    public static byte[] Decrypt(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid encrypted data.");

        byte[] nonce = encryptedData[..NonceSize];
        byte[] tag = encryptedData[NonceSize..(NonceSize + TagSize)];
        byte[] ciphertext = encryptedData[(NonceSize + TagSize)..];
        byte[] plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
