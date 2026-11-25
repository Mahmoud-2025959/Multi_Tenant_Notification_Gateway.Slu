using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationGateway.Application.Abstractions;

namespace NotificationGateway.Infrastructure.Security;

/// <summary>
/// Master keys come from configuration (environment variables / Docker secrets / Azure Key Vault in production),
/// never from the database and never from source control.
///
///   "SecretProtection": {
///     "ActiveKeyId": "k2",
///     "Keys": { "k1": "&lt;base64 32 bytes&gt;", "k2": "&lt;base64 32 bytes&gt;" }
///   }
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "SecretProtection";
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new();
}

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceSize = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;     // AesGcm.TagByteSizes.MaxSize

    private readonly string _activeKeyId;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ActiveKeyId) || !o.Keys.ContainsKey(o.ActiveKeyId))
            throw new InvalidOperationException("SecretProtection:ActiveKeyId must reference a configured key.");

        _keys = o.Keys.ToDictionary(k => k.Key, k =>
        {
            var bytes = Convert.FromBase64String(k.Value);
            if (bytes.Length != 32) throw new InvalidOperationException($"Secret key '{k.Key}' must be 32 bytes (AES-256).");
            return bytes;
        });
        _activeKeyId = o.ActiveKeyId;
    }

    public string Protect(string plaintext, string binding)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = _keys[_activeKeyId];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag, Encoding.UTF8.GetBytes(binding));
        }

        CryptographicOperations.ZeroMemory(plainBytes);

        var payload = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, NonceSize + cipher.Length, TagSize);

        return $"{Version}:{_activeKeyId}:{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(string envelope, string binding)
    {
        var (keyId, payload) = Parse(envelope);
        if (!_keys.TryGetValue(keyId, out var key))
            throw new CryptographicException($"Unknown secret key id '{keyId}'.");
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Secret envelope is too short.");

        var cipherLength = payload.Length - NonceSize - TagSize;
        var nonce = payload.AsSpan(0, NonceSize);
        var cipher = payload.AsSpan(NonceSize, cipherLength);
        var tag = payload.AsSpan(NonceSize + cipherLength, TagSize);
        var plain = new byte[cipherLength];

        using (var aes = new AesGcm(key, TagSize))
        {
            // Throws AuthenticationTagMismatchException if the envelope was tampered with OR bound to another row.
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(binding));
        }

        var result = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }

    public bool NeedsReEncryption(string envelope) => Parse(envelope).KeyId != _activeKeyId;

    private static (string KeyId, byte[] Payload) Parse(string envelope)
    {
        var parts = envelope.Split(':', 3);
        if (parts.Length != 3 || parts[0] != Version)
            throw new CryptographicException("Unsupported secret envelope format.");
        return (parts[1], Convert.FromBase64String(parts[2]));
    }
}
