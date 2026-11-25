namespace NotificationGateway.Domain.Outbox;

/// <summary>
/// Transactional outbox. Written in the SAME transaction as the notification + deliveries, then published to RabbitMQ
/// by a background relay. If the app crashes after commit but before publish, the relay publishes it after restart.
///
/// RabbitMQ messages are only a "wake-up" signal for dispatchers. The database delivery row is the source of truth,
/// so a lost or duplicated broker message cannot lose or duplicate a notification (the DB poller + lease cover it).
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(Guid tenantId, string type, string payload, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        Error = null;
        LockedUntil = null;
    }

    public void MarkFailed(string error, DateTimeOffset retryAt)
    {
        Attempts++;
        Error = error.Length > 1000 ? error[..1000] : error;
        LockedUntil = retryAt;
    }
}
