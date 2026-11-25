namespace NotificationGateway.Domain.Enums;

/// <summary>
/// Lifecycle of ONE delivery (one recipient on one channel).
/// Accepted/Queued are set inside the same transaction as the API request.
/// Sent  = a provider ACCEPTED the request (not proof the recipient got it).
/// Delivered = confirmed by provider callback/receipt, or by an internal channel that can prove it (InApp persisted).
/// Uncertain = provider may or may not have sent it (e.g. timeout after the request left us). We never fail over
///             from this state because that could produce a duplicate message; we wait for a webhook / reconciliation.
/// </summary>
public enum DeliveryStatus
{
    Accepted = 0,
    Queued = 1,
    Processing = 2,
    Sent = 3,
    Delivered = 4,
    Failed = 5,
    Retrying = 6,
    DeadLettered = 7,
    Uncertain = 8,
    Suppressed = 9,
    Cancelled = 10
}
