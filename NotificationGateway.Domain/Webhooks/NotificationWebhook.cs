using NotificationGateway.Domain.Common;

namespace NotificationGateway.Domain.Webhooks;

/// <summary>
/// Callback endpoint registered for a provider configuration.
/// The public URL is /api/webhooks/{providerKey}/{PublicToken}. The token is random and unguessable and maps the
/// callback to exactly one tenant + configuration, so callback OWNERSHIP is established before any payload is trusted:
/// a delivery is only updated if its ProviderMessageId belongs to that same tenant and configuration.
/// </summary>
public sealed class NotificationWebhook : Entity, ITenantOwned
{
    private NotificationWebhook() { }

    public NotificationWebhook(Guid tenantId, Guid providerConfigurationId, string providerKey, string publicToken, string encryptedSigningSecret, DateTimeOffset now)
    {
        TenantId = tenantId;
        ProviderConfigurationId = providerConfigurationId;
        ProviderKey = providerKey;
        PublicToken = publicToken;
        EncryptedSigningSecret = encryptedSigningSecret;
        IsActive = true;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid ProviderConfigurationId { get; private set; }
    public string ProviderKey { get; private set; } = default!;
    public string PublicToken { get; private set; } = default!;

    /// <summary>HMAC signing secret (or provider auth token) used to verify callbacks; encrypted at rest.</summary>
    public string EncryptedSigningSecret { get; private set; } = default!;

    /// <summary>During rotation the previous secret stays valid for a grace period so in-flight callbacks are not rejected.</summary>
    public string? PreviousEncryptedSigningSecret { get; private set; }
    public DateTimeOffset? PreviousSecretValidUntil { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void RotateSecret(string newEncryptedSecret, TimeSpan gracePeriod, DateTimeOffset now)
    {
        PreviousEncryptedSigningSecret = EncryptedSigningSecret;
        PreviousSecretValidUntil = now.Add(gracePeriod);
        EncryptedSigningSecret = newEncryptedSecret;
    }

    public void Deactivate() => IsActive = false;
}
