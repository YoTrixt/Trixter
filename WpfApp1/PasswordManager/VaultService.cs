using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfApp1.PasswordManager;

public class VaultService
{
    private readonly string vaultDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trixter",
            "PasswordManager");

    private readonly string vaultPath;

    private byte[]? encryptionKey;
    private byte[]? salt;

    private List<VaultEntry> entries = new();

    public bool IsUnlocked => encryptionKey != null;

    public VaultService()
    {
        vaultPath = Path.Combine(vaultDirectory, "vault.json");
    }

    public bool VaultExists()
    {
        return File.Exists(vaultPath);
    }

    public void CreateVault(string masterPassword)
    {
        if (VaultExists())
            throw new InvalidOperationException("A vault already exists.");

        if (string.IsNullOrWhiteSpace(masterPassword) || masterPassword.Length < 12)
            throw new ArgumentException("Master password must be at least 12 characters.");

        Directory.CreateDirectory(vaultDirectory);

        salt = VaultCrypto.CreateSalt();
        encryptionKey = VaultCrypto.DeriveKey(masterPassword, salt);

        entries = new List<VaultEntry>();

        Save();
    }

    public void Unlock(string masterPassword)
    {
        if (!VaultExists())
            throw new InvalidOperationException("No vault exists.");

        var json = File.ReadAllText(vaultPath);
        var vault = JsonSerializer.Deserialize<VaultFile>(json)
            ?? throw new InvalidDataException("Invalid vault file.");

        if (vault.Version != 1)
            throw new InvalidDataException("Unsupported vault version.");

        byte[] loadedSalt = Convert.FromBase64String(vault.Salt);
        byte[] encryptedData = Convert.FromBase64String(vault.EncryptedData);

        byte[] key = VaultCrypto.DeriveKey(masterPassword, loadedSalt);

        try
        {
            byte[] decryptedData = VaultCrypto.Decrypt(encryptedData, key);

            entries = JsonSerializer.Deserialize<List<VaultEntry>>(decryptedData)
                ?? new List<VaultEntry>();

            salt = loadedSalt;
            encryptionKey = key;
            key = Array.Empty<byte>();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Save()
    {
        if (!IsUnlocked || salt == null)
            throw new InvalidOperationException("Vault is locked.");

        Directory.CreateDirectory(vaultDirectory);

        byte[] jsonData = JsonSerializer.SerializeToUtf8Bytes(entries);
        byte[] encryptedData = VaultCrypto.Encrypt(jsonData, encryptionKey!);

        var vault = new VaultFile
        {
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            EncryptedData = Convert.ToBase64String(encryptedData)
        };

        string json = JsonSerializer.Serialize(vault);

        string tempPath = vaultPath + ".tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, vaultPath, true);

        CryptographicOperations.ZeroMemory(jsonData);
    }

    public List<VaultEntry> GetEntries()
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");

        return entries.ToList();
    }

    public List<VaultEntry> GetEntriesForWebsite(string website)
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");

        if (!Uri.TryCreate(website, UriKind.Absolute, out var uri))
            return new List<VaultEntry>();

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
            return new List<VaultEntry>();

        string origin = uri.GetLeftPart(UriPartial.Authority)
            .TrimEnd('/');

        return entries
            .Where(entry =>
                string.Equals(
                    entry.Website.TrimEnd('/'),
                    origin,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void AddEntry(VaultEntry entry)
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");

        entry.CreatedAt = DateTime.UtcNow;
        entry.ModifiedAt = DateTime.UtcNow;

        entries.Add(entry);
        Save();
    }

    public void UpdateEntry(VaultEntry updatedEntry)
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");

        var existing = entries.FirstOrDefault(e => e.Id == updatedEntry.Id);

        if (existing == null)
            throw new InvalidOperationException("Entry not found.");

        existing.Website = updatedEntry.Website;
        existing.Username = updatedEntry.Username;
        existing.Password = updatedEntry.Password;
        existing.Notes = updatedEntry.Notes;
        existing.ModifiedAt = DateTime.UtcNow;

        Save();
    }

    public void DeleteEntry(Guid id)
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");

        entries.RemoveAll(e => e.Id == id);
        Save();
    }

    public void Lock()
    {
        if (encryptionKey != null)
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            encryptionKey = null;
        }

        salt = null;
        entries.Clear();
    }

    private class VaultFile
    {
        public int Version { get; set; }

        public string Salt { get; set; } = "";

        public string EncryptedData { get; set; } = "";
    }
}