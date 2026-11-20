using System.Security.Cryptography;
using System.Text;
using Managerr.Common.Paths;

namespace Managerr.Common.Crypto;

public sealed record MaskedSecret(bool IsSet, string? MaskedHint);

public interface ISecretProtector
{
    string KeyId { get; }
    string Protect(string plaintext);
    string Unprotect(string envelope);
    bool IsProtectedEnvelope(string? value);
    MaskedSecret Mask(string? rawOrProtectedValue);
    byte[] DeriveSubkey(string contextInfo, int lengthBytes = 32);
}

/// <summary>
/// Envelope AES-256-GCM secret protector:
/// - KEK is derived via HKDF-SHA256 from MANAGERR_MASTER_KEY, /run/secrets/managerr_master_key, or /config/master.key.
/// - Every Protect() call generates a fresh 256-bit DEK and wraps both the DEK and payload with unique 96-bit nonces.
/// - Format: enc:v1:{keyId}:{dekNonce+dekCipher+dekTag base64url}:{dataNonce+dataCipher+dataTag base64url}
/// </summary>
public sealed class EnvelopeSecretProtector : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly byte[] _masterSeed;
    private readonly byte[] _kek;

    public EnvelopeSecretProtector(IAppPaths paths, string? explicitMasterKey = null)
    {
        _masterSeed = LoadOrCreateMasterSeed(paths, explicitMasterKey);
        _kek = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: _masterSeed,
            outputLength: KeyBytes,
            salt: "managerr.envelope.salt.v1"u8.ToArray(),
            info: "managerr.kek.v1"u8.ToArray());

        var fp = SHA256.HashData(_kek);
        KeyId = Convert.ToHexStringLower(fp[..4]);
    }

    public string KeyId { get; }

    public bool IsProtectedEnvelope(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (IsProtectedEnvelope(plaintext))
        {
            return plaintext;
        }

        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            var wrappedDek = EncryptAesGcm(_kek, dek, associatedData: Encoding.ASCII.GetBytes($"dek:{KeyId}"));
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var encryptedPayload = EncryptAesGcm(dek, plainBytes, associatedData: Encoding.ASCII.GetBytes($"val:{KeyId}"));

            return $"{Prefix}{KeyId}:{ToBase64Url(wrappedDek)}:{ToBase64Url(encryptedPayload)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public string Unprotect(string envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope);
        if (!IsProtectedEnvelope(envelope))
        {
            throw new CryptographicException("Value is not a valid Managerr encrypted secret envelope.");
        }

        var parts = envelope[Prefix.Length..].Split(':');
        if (parts.Length != 3)
        {
            throw new CryptographicException("Malformed secret envelope structure.");
        }

        var keyId = parts[0];
        var wrappedDek = FromBase64Url(parts[1]);
        var encryptedPayload = FromBase64Url(parts[2]);

        var dek = DecryptAesGcm(_kek, wrappedDek, associatedData: Encoding.ASCII.GetBytes($"dek:{keyId}"));
        try
        {
            var plainBytes = DecryptAesGcm(dek, encryptedPayload, associatedData: Encoding.ASCII.GetBytes($"val:{keyId}"));
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public MaskedSecret Mask(string? rawOrProtectedValue)
    {
        if (string.IsNullOrWhiteSpace(rawOrProtectedValue))
        {
            return new MaskedSecret(false, null);
        }

        var plain = IsProtectedEnvelope(rawOrProtectedValue)
            ? Unprotect(rawOrProtectedValue)
            : rawOrProtectedValue;

        if (string.IsNullOrEmpty(plain))
        {
            return new MaskedSecret(false, null);
        }

        var suffix = plain.Length <= 4 ? new string('*', plain.Length) : $"****{plain[^4..]}";
        return new MaskedSecret(true, suffix);
    }

    public byte[] DeriveSubkey(string contextInfo, int lengthBytes = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextInfo);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: _masterSeed,
            outputLength: lengthBytes,
            salt: "managerr.subkey.salt.v1"u8.ToArray(),
            info: Encoding.UTF8.GetBytes(contextInfo));
    }

    private static byte[] EncryptAesGcm(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        nonce.CopyTo(output.AsSpan(0, NonceBytes));

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(
            nonce: nonce,
            plaintext: plaintext,
            ciphertext: output.AsSpan(NonceBytes, plaintext.Length),
            tag: output.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            associatedData: associatedData);

        return output;
    }

    private static byte[] DecryptAesGcm(byte[] key, ReadOnlySpan<byte> combined, ReadOnlySpan<byte> associatedData)
    {
        if (combined.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Ciphertext payload truncated.");
        }

        var cipherLen = combined.Length - NonceBytes - TagBytes;
        var plaintext = new byte[cipherLen];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(
            nonce: combined[..NonceBytes],
            ciphertext: combined.Slice(NonceBytes, cipherLen),
            tag: combined.Slice(NonceBytes + cipherLen, TagBytes),
            plaintext: plaintext,
            associatedData: associatedData);

        return plaintext;
    }

    private static byte[] LoadOrCreateMasterSeed(IAppPaths paths, string? explicitMasterKey)
    {
        var raw = explicitMasterKey ?? Environment.GetEnvironmentVariable("MANAGERR_MASTER_KEY");

        if (string.IsNullOrWhiteSpace(raw) && File.Exists("/run/secrets/managerr_master_key"))
        {
            raw = File.ReadAllText("/run/secrets/managerr_master_key").Trim();
        }

        if (string.IsNullOrWhiteSpace(raw) && File.Exists(paths.MasterKeyFilePath))
        {
            raw = File.ReadAllText(paths.MasterKeyFilePath).Trim();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            var generated = RandomNumberGenerator.GetBytes(32);
            raw = Convert.ToBase64String(generated);
            File.WriteAllText(paths.MasterKeyFilePath, raw);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(paths.MasterKeyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Best-effort on filesystems without POSIX chmod support (e.g., certain FUSE/SMB mounts).
                }
            }
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
