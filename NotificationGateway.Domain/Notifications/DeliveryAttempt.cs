using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Notifications;

/// <summary>
/// Immutable history of every provider call (append-only). Outcome == null means the call was in flight
/// when the row was written; it is completed after the provider returns.
/// </summary>
public sealed class DeliveryAttempt : Entity, ITenantOwned
{
    private DeliveryAttempt() { }

    public DeliveryAttempt(Guid tenantId, Guid deliveryId, int attemptNumber, Guid providerConfigurationId, string providerKey, DateTimeOffset startedAt)
    {
        TenantId = tenantId;
        DeliveryId = deliveryId;
        AttemptNumber = attemptNumber;
        ProviderConfigurationId = providerConfigurationId;
        ProviderKey = providerKey;
        StartedAt = startedAt;
    }

    public Guid TenantId { get; private set; }
    public Guid DeliveryId { get; private set; }
    public int AttemptNumber { get; private set; }
    public Guid ProviderConfigurationId { get; private set; }
    public string ProviderKey { get; private set; } = default!;
    public AttemptOutcome? Outcome { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int? HttpStatus { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long? DurationMs { get; private set; }

    public void Complete(AttemptOutcome outcome, string? providerMessageId, string? errorCode, string? errorMessage, int? httpStatus, DateTimeOffset completedAt)
    {
        if (Outcome is not null) throw new DomainException("attempt.already_completed", "Attempt already completed.");
        Outcome = outcome;
        ProviderMessageId = providerMessageId;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage is { Length: > 1000 } ? errorMessage[..1000] : errorMessage;
        HttpStatus = httpStatus;
        CompletedAt = completedAt;
        DurationMs = (long)(completedAt - StartedAt).TotalMilliseconds;
    }
}
