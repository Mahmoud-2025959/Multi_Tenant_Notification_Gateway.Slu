using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Notifications;

/// <summary>
/// One recipient on one channel. This is the unit that is queued, leased, retried, failed over and dead-lettered.
///
/// State machine (see docs/Notification_Routing_and_Fallback.md):
///
///   Queued ──lease──▶ Processing ──accepted──▶ Sent ──receipt──▶ Delivered
///     ▲                  │  │  │                 │
///     │ defer            │  │  └─uncertain──▶ Uncertain ──receipt──▶ Delivered | Failed
///     │                  │  └─transient/provider failure──▶ Retrying ──lease──▶ Processing
///     │                  └─recipient rejected──▶ Failed          │
///     └──────────────────────────────────────── max attempts ────┴──▶ DeadLettered
///
/// Synchronous channels (InApp) go straight from Processing to Delivered.
/// </summary>
public sealed class NotificationDelivery : Entity, ITenantOwned
{
    private NotificationDelivery() { }

    internal NotificationDelivery(Guid tenantId, Guid notificationId, Guid recipientId, NotificationChannel channel,
        NotificationPriority priority, int maxAttempts, DateTimeOffset now)
    {
        TenantId = tenantId;
        NotificationId = notificationId;
        RecipientId = recipientId;
        Channel = channel;
        Priority = priority;
        MaxAttempts = maxAttempts;
        Status = DeliveryStatus.Queued;
        NextAttemptAt = now;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid RecipientId { get; private set; }
    public NotificationRecipient Recipient { get; private set; } = default!;
    public Notification Notification { get; private set; } = default!;

    public NotificationChannel Channel { get; private set; }
    public NotificationPriority Priority { get; private set; }
    public DeliveryStatus Status { get; private set; }

    /// <summary>Number of processing rounds that reached a provider decision. Compared with <see cref="MaxAttempts"/>.</summary>
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; }

    /// <summary>Position in the tenant's provider chain for this channel (0 = primary).</summary>
    public int ProviderIndex { get; private set; }

    /// <summary>How many times the whole chain was exhausted. Drives exponential backoff.</summary>
    public int RetryRound { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    // ---- Lease (prevents concurrent dispatchers from processing the same delivery: T16) ----
    public string? LockedBy { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Write-ahead marker: set and COMMITTED before calling a provider, cleared after the result is recorded.
    /// If a worker crashes and the lease expires while this is set, we cannot know whether the provider received
    /// the request, so recovery treats it as Uncertain (unless the provider honours idempotency keys). T12.
    /// </summary>
    public Guid? InFlightProviderConfigurationId { get; private set; }

    public Guid? LastProviderConfigurationId { get; private set; }
    public string? LastProviderKey { get; private set; }

    /// <summary>The provider's own id for the message; used to match webhook callbacks.</summary>
    public string? ProviderMessageId { get; private set; }

    public string? LastError { get; private set; }

    // ---- Rendered content snapshot (rendered once at accept time so retries/fallbacks send identical content) ----
    public Guid? TemplateId { get; private set; }
    public int? TemplateVersion { get; private set; }
    public TemplateLanguage? Language { get; private set; }
    public string? Address { get; private set; }
    public string? Subject { get; private set; }
    public string? Body { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    /// <summary>Stable key sent to providers that support idempotency, so a retry after an uncertain result is de-duplicated by the provider.</summary>
    public string ProviderIdempotencyKey => $"ng-{Id:N}";

    public bool IsTerminal => Status is DeliveryStatus.Delivered or DeliveryStatus.Failed or DeliveryStatus.DeadLettered
        or DeliveryStatus.Suppressed or DeliveryStatus.Cancelled;

    public bool AttemptsExhausted => AttemptCount >= MaxAttempts;

    // ------------------------------------------------------------------ transitions

    public void SetRenderedContent(Guid templateId, int templateVersion, TemplateLanguage language, string? address, string? subject, string body)
    {
        TemplateId = templateId;
        TemplateVersion = templateVersion;
        Language = language;
        Address = address;
        Subject = subject;
        Body = body;
    }

    public void StartProcessing(string workerId, DateTimeOffset now, TimeSpan lease)
    {
        if (Status is not (DeliveryStatus.Queued or DeliveryStatus.Retrying))
            throw new DomainException("delivery.invalid_transition", $"Cannot start processing from {Status}.");

        Status = DeliveryStatus.Processing;
        LockedBy = workerId;
        LockedUntil = now.Add(lease);
        UpdatedAt = now;
    }

    /// <summary>Persisted (committed) immediately before the provider call. See <see cref="InFlightProviderConfigurationId"/>.</summary>
    public void MarkCallInFlight(Guid providerConfigurationId, string providerKey, DateTimeOffset now)
    {
        EnsureProcessing();
        InFlightProviderConfigurationId = providerConfigurationId;
        LastProviderConfigurationId = providerConfigurationId;
        LastProviderKey = providerKey;
        AttemptCount++;
        UpdatedAt = now;
    }

    public void RecordAccepted(string? providerMessageId, bool synchronousDelivery, DateTimeOffset now)
    {
        EnsureProcessing();
        ProviderMessageId = providerMessageId;
        SentAt = now;
        LastError = null;
        if (synchronousDelivery)
        {
            Status = DeliveryStatus.Delivered;
            DeliveredAt = now;
            CompletedAt = now;
        }
        else
        {
            Status = DeliveryStatus.Sent;
        }
        ClearLease(now);
    }

    /// <summary>Retry later, either on the same provider or on another one in the chain.</summary>
    public void ScheduleRetry(int providerIndex, bool chainExhausted, DateTimeOffset nextAttemptAt, string error, DateTimeOffset now)
    {
        EnsureProcessing();
        ProviderIndex = providerIndex;
        if (chainExhausted) RetryRound++;
        NextAttemptAt = nextAttemptAt;
        LastError = Truncate(error);
        Status = DeliveryStatus.Retrying;
        ClearLease(now);
    }

    /// <summary>The provider may have sent it. We stop and wait for a receipt / reconciliation instead of failing over.</summary>
    public void MarkUncertain(string error, DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.Processing))
            throw new DomainException("delivery.invalid_transition", $"Cannot mark uncertain from {Status}.");
        Status = DeliveryStatus.Uncertain;
        LastError = Truncate(error);
        NextAttemptAt = null;
        ClearLease(now);
    }

    public void Fail(string error, DateTimeOffset now)
    {
        if (IsTerminal) return;
        Status = DeliveryStatus.Failed;
        LastError = Truncate(error);
        NextAttemptAt = null;
        CompletedAt = now;
        ClearLease(now);
    }

    public void DeadLetter(string reason, DateTimeOffset now)
    {
        if (IsTerminal) return;
        Status = DeliveryStatus.DeadLettered;
        LastError = Truncate(reason);
        NextAttemptAt = null;
        CompletedAt = now;
        ClearLease(now);
    }

    /// <summary>Quiet hours or quota (Defer policy): keep it queued until a later time. Does not consume an attempt.</summary>
    public void Defer(DateTimeOffset until, string reason, DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.Queued or DeliveryStatus.Retrying or DeliveryStatus.Processing))
            throw new DomainException("delivery.invalid_transition", $"Cannot defer from {Status}.");
        Status = DeliveryStatus.Queued;
        NextAttemptAt = until;
        LastError = reason;
        ClearLease(now);
    }

    /// <summary>User opted out of this channel/type at dispatch time.</summary>
    public void Suppress(string reason, DateTimeOffset now)
    {
        if (IsTerminal) return;
        Status = DeliveryStatus.Suppressed;
        LastError = reason;
        NextAttemptAt = null;
        CompletedAt = now;
        ClearLease(now);
    }

    public bool TryCancel(DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.Queued or DeliveryStatus.Retrying or DeliveryStatus.Accepted)) return false;
        Status = DeliveryStatus.Cancelled;
        NextAttemptAt = null;
        CompletedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Provider receipt said delivered. Idempotent; out of order receipts never move a terminal state backwards.</summary>
    public bool ConfirmDelivered(DateTimeOffset at, DateTimeOffset now)
    {
        if (Status is DeliveryStatus.Delivered) return false;
        if (Status is not (DeliveryStatus.Sent or DeliveryStatus.Uncertain)) return false;
        Status = DeliveryStatus.Delivered;
        DeliveredAt = at;
        CompletedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Provider receipt said undeliverable. We do NOT fail over automatically: the same address is very likely
    /// to fail with another provider, and some receipts arrive after the user already got the message elsewhere.
    /// </summary>
    public bool ConfirmFailedByProvider(string reason, DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.Sent or DeliveryStatus.Uncertain)) return false;
        Status = DeliveryStatus.Failed;
        LastError = Truncate(reason);
        CompletedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// A worker died holding the lease. If no provider call was in flight it is safe to retry.
    /// If a call WAS in flight, retry only when the provider de-duplicates by idempotency key; otherwise mark Uncertain.
    /// </summary>
    public void RecoverExpiredLease(bool inFlightProviderSupportsIdempotency, DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Processing || LockedUntil is null || LockedUntil > now)
            throw new DomainException("delivery.lease_not_expired", "Lease has not expired.");

        if (InFlightProviderConfigurationId is not null && !inFlightProviderSupportsIdempotency)
        {
            MarkUncertain("Worker stopped while a provider call was in flight; outcome unknown.", now);
            return;
        }

        Status = DeliveryStatus.Retrying;
        NextAttemptAt = now;
        LastError = "Recovered after worker lease expired.";
        ClearLease(now);
    }

    /// <summary>Admin replay from the dead-letter queue.</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.DeadLettered or DeliveryStatus.Failed or DeliveryStatus.Uncertain))
            throw new DomainException("delivery.invalid_transition", $"Cannot requeue from {Status}.");
        Status = DeliveryStatus.Queued;
        AttemptCount = 0;
        ProviderIndex = 0;
        RetryRound = 0;
        NextAttemptAt = now;
        CompletedAt = null;
        UpdatedAt = now;
    }

    private void EnsureProcessing()
    {
        if (Status != DeliveryStatus.Processing)
            throw new DomainException("delivery.not_processing", $"Delivery is {Status}, expected Processing.");
    }

    private void ClearLease(DateTimeOffset now)
    {
        LockedBy = null;
        LockedUntil = null;
        InFlightProviderConfigurationId = null;
        UpdatedAt = now;
    }

    private static string Truncate(string value) => value.Length > 1000 ? value[..1000] : value;
}
