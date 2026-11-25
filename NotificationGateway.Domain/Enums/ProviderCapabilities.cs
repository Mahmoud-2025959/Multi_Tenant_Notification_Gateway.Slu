namespace NotificationGateway.Domain.Enums;

/// <summary>
/// Providers do NOT all support the same things. The engine reads these flags instead of assuming.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    /// <summary>Provider sends delivery receipts (webhook) so we can move Sent -> Delivered/Failed.</summary>
    DeliveryReceipts = 1,
    /// <summary>Provider de-duplicates on an idempotency key we send, so retrying after an uncertain result is safe.</summary>
    IdempotencyKey = 2,
    /// <summary>Provider exposes a status-query API we can use to reconcile Uncertain deliveries.</summary>
    StatusQuery = 4,
    /// <summary>The channel is internal and success means delivered (e.g. InApp row persisted).</summary>
    SynchronousDelivery = 8,
    /// <summary>Supports a custom sender identity (From address, alphanumeric sender id).</summary>
    SenderIdentity = 16
}
