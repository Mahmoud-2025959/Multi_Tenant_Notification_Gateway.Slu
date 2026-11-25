using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Providers;

/// <summary>
/// A configured instance of a provider product.
/// TenantId == null  => shared configuration owned by the platform (e.g. the platform SMTP relay) that tenants may use.
/// TenantId != null  => owned by that tenant only.
///
/// Secrets (API keys, passwords) are NEVER stored here in plain text: <see cref="EncryptedSecrets"/> holds an
/// AES-GCM envelope produced by ISecretProtector. Non-secret settings (host, port, region) live in <see cref="SettingsJson"/>.
/// </summary>
public sealed class TenantProviderConfiguration : Entity, IOptionallyTenantOwned
{
    private TenantProviderConfiguration() { }

    public TenantProviderConfiguration(
        Guid? tenantId,
        string providerKey,
        NotificationChannel channel,
        string name,
        string settingsJson,
        string encryptedSecrets,
        string? senderIdentity,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) throw new DomainException("provider.key_required", "Provider key is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("provider.name_required", "Configuration name is required.");

        TenantId = tenantId;
        ProviderKey = providerKey;
        Channel = channel;
        Name = name;
        SettingsJson = settingsJson;
        EncryptedSecrets = encryptedSecrets;
        SecretVersion = 1;
        SecretsUpdatedAt = now;
        SenderIdentity = senderIdentity;
        IsActive = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid? TenantId { get; private set; }
    public bool IsShared => TenantId is null;

    public string ProviderKey { get; private set; } = default!;
    public NotificationChannel Channel { get; private set; }
    public string Name { get; private set; } = default!;

    /// <summary>Non-secret JSON settings (validated by the adapter).</summary>
    public string SettingsJson { get; private set; } = "{}";

    /// <summary>Encrypted envelope: "v1:{keyId}:{base64(nonce|cipher|tag)}". Never returned by the API.</summary>
    public string EncryptedSecrets { get; private set; } = default!;

    /// <summary>Incremented on every rotation. Exposed (not the secret) so admins can see a rotation happened.</summary>
    public int SecretVersion { get; private set; }
    public DateTimeOffset SecretsUpdatedAt { get; private set; }

    /// <summary>From address / sender id / WhatsApp phone number id, where the provider supports it.</summary>
    public string? SenderIdentity { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (SQL rowversion).</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public void Update(string name, string settingsJson, string? senderIdentity, bool isActive, DateTimeOffset now)
    {
        Name = name;
        SettingsJson = settingsJson;
        SenderIdentity = senderIdentity;
        IsActive = isActive;
        UpdatedAt = now;
    }

    /// <summary>
    /// Rotation replaces the envelope atomically. In-flight dispatches that already decrypted the old secret finish with it;
    /// every dispatch after commit decrypts the new one (secrets are never cached longer than one dispatch).
    /// </summary>
    public void RotateSecrets(string newEncryptedSecrets, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newEncryptedSecrets)) throw new DomainException("provider.secret_required", "Secret envelope is required.");
        EncryptedSecrets = newEncryptedSecrets;
        SecretVersion++;
        SecretsUpdatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Used by master-key rotation: re-encrypts with a new key WITHOUT changing the secret value or version.</summary>
    public void ReplaceEnvelope(string reEncrypted) => EncryptedSecrets = reEncrypted;

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}
