using System.Text.Json;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Application.Providers;

/// <summary>
/// Adapter contract for one provider PRODUCT (SMTP, SendGrid, Twilio SMS, ...).
///
/// Adapters are stateless singletons: everything tenant-specific (settings, decrypted secrets, sender identity)
/// arrives in <see cref="NotificationMessage.Configuration"/> for that one call. That is how tenant A can use
/// SendGrid while tenant B uses SMTP through the same engine, and why an adapter can never "remember" another
/// tenant's credentials.
///
/// Adding a provider = implement this interface + register it in DI. The orchestrator, router and dispatcher
/// never change (no switch statements; resolution is a dictionary lookup by <see cref="ProviderName"/>).
/// </summary>
public interface INotificationProvider
{
    /// <summary>Stable key, e.g. "sendgrid". Matches NotificationProvider.Key and TenantProviderConfiguration.ProviderKey.</summary>
    string ProviderName { get; }

    NotificationChannel Channel { get; }

    ProviderDescriptor Descriptor { get; }

    /// <summary>Validates a tenant's settings/secrets BEFORE they are saved (required fields, formats, SSRF-safe hosts).</summary>
    IReadOnlyList<string> ValidateConfiguration(JsonElement settings, IReadOnlyDictionary<string, string> secrets);

    /// <summary>
    /// Sends one message. MUST NOT throw for expected provider errors: classify them into <see cref="NotificationResult"/>.
    /// MUST honour <paramref name="cancellationToken"/> (the dispatcher uses it to enforce the per-call timeout).
    /// </summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

public sealed record ProviderDescriptor(
    string DisplayName,
    ProviderCapabilities Capabilities,
    IReadOnlyList<string> RequiredSettings,
    IReadOnlyList<string> RequiredSecrets)
{
    public bool Supports(ProviderCapabilities capability) => (Capabilities & capability) == capability;
}

/// <summary>Per-call, decrypted view of a TenantProviderConfiguration. Lives only for the duration of one SendAsync.</summary>
public sealed record ProviderConfigurationContext(
    Guid ConfigurationId,
    Guid? OwnerTenantId,
    JsonElement Settings,
    IReadOnlyDictionary<string, string> Secrets,
    string? SenderIdentity)
{
    public string? Setting(string name) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ToString()
            : null;

    public string? Secret(string name) => Secrets.TryGetValue(name, out var v) ? v : null;

    /// <summary>Never print secrets, even by accident.</summary>
    public override string ToString() => $"ProviderConfiguration {{ Id = {ConfigurationId}, Secrets = *** }}";
}

public sealed record NotificationMessage(
    Guid TenantId,
    Guid NotificationId,
    Guid DeliveryId,
    string IdempotencyKey,
    NotificationChannel Channel,
    string NotificationType,
    NotificationPriority Priority,
    string? RecipientUserId,
    string? Address,
    string? Subject,
    string Body,
    ProviderConfigurationContext Configuration);

/// <summary>
/// Normalised result. The engine decides retry/fallback ONLY from <see cref="Outcome"/>, never from provider specific codes.
/// </summary>
public sealed record NotificationResult(
    AttemptOutcome Outcome,
    string? ProviderMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? HttpStatus = null)
{
    public static NotificationResult Accepted(string? providerMessageId, int? httpStatus = null) =>
        new(AttemptOutcome.Accepted, providerMessageId, HttpStatus: httpStatus);

    public static NotificationResult Transient(string code, string message, int? httpStatus = null) =>
        new(AttemptOutcome.TransientFailure, null, code, message, httpStatus);

    public static NotificationResult ProviderFailure(string code, string message, int? httpStatus = null) =>
        new(AttemptOutcome.ProviderFailure, null, code, message, httpStatus);

    public static NotificationResult RecipientRejected(string code, string message, int? httpStatus = null) =>
        new(AttemptOutcome.RecipientRejected, null, code, message, httpStatus);

    public static NotificationResult Uncertain(string code, string message) =>
        new(AttemptOutcome.Uncertain, null, code, message);
}
