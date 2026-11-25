namespace NotificationGateway.Application.Abstractions;

/// <summary>
/// Runs work in ONE database transaction using the configured execution strategy (SQL retry-on-failure),
/// so the whole block is retried as a unit instead of half of it.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>
/// SQL-level leasing of due deliveries (UPDLOCK + READPAST). Two dispatchers can never lease the same row (T16).
/// Leased rows are already in status Processing with LockedBy = workerId when this returns.
/// </summary>
public interface IDeliveryLeaseStore
{
    Task<IReadOnlyList<Guid>> LeaseDueAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FindExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Atomic quota counter: "UPDATE ... SET Used = Used + n WHERE Used + n &lt;= Limit". Runs inside the caller's
/// transaction, so a rolled-back request also rolls back the consumption.
/// </summary>
public interface IQuotaCounter
{
    Task<bool> TryConsumeAsync(Guid tenantId, string channelKey, string periodKey, long amount, long limit, CancellationToken cancellationToken);
}

/// <summary>Replay protection for provider callbacks. Returns false when (providerKey, eventId) was already recorded.</summary>
public interface IWebhookEventStore
{
    Task<bool> TryRecordAsync(string providerKey, string eventId, Guid tenantId, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}

/// <summary>Real-time push to connected clients (implemented with SignalR in the API host).</summary>
public interface IRealtimePublisher
{
    Task PublishToUserAsync(Guid tenantId, string userId, RealtimeMessage message, CancellationToken cancellationToken);
}

public sealed record RealtimeMessage(
    Guid DeliveryId,
    Guid NotificationId,
    string NotificationType,
    string Priority,
    string? Title,
    string Body,
    DateTimeOffset SentAt);

/// <summary>Wakes local dispatchers right after new work is committed (in addition to RabbitMQ and polling).</summary>
public interface IDispatchSignal
{
    void Notify();
}

/// <summary>
/// SSRF protection for tenant-supplied provider endpoints (settings keys such as baseUrl, host, statusCallbackUrl).
/// Returns validation errors; empty means safe. Outbound provider HTTP clients ALSO re-check every resolved IP at
/// connect time, so DNS rebinding after validation cannot reach internal addresses.
/// </summary>
public interface IProviderEndpointValidator
{
    Task<IReadOnlyList<string>> ValidateAsync(System.Text.Json.JsonElement settings, CancellationToken cancellationToken);
}
