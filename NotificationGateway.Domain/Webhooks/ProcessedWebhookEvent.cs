namespace NotificationGateway.Domain.Webhooks;

/// <summary>
/// Replay protection: UNIQUE (ProviderKey, EventId). Inserting the same provider event twice fails on the
/// unique index, so a replayed callback is acknowledged but never applied twice.
/// </summary>
public sealed class ProcessedWebhookEvent
{
    private ProcessedWebhookEvent() { }

    public ProcessedWebhookEvent(string providerKey, string eventId, Guid tenantId, DateTimeOffset receivedAt)
    {
        ProviderKey = providerKey;
        EventId = eventId;
        TenantId = tenantId;
        ReceivedAt = receivedAt;
    }

    public long Id { get; private set; }
    public string ProviderKey { get; private set; } = default!;
    public string EventId { get; private set; } = default!;
    public Guid TenantId { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
}
