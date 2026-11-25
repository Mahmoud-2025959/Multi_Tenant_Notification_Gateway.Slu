using NotificationGateway.Domain.Common;

namespace NotificationGateway.Domain.Notifications;

/// <summary>The InApp channel's "inbox" row. Written by the InApp provider; read by the user's app.</summary>
public sealed class InAppNotification : Entity, ITenantOwned
{
    private InAppNotification() { }

    public InAppNotification(Guid tenantId, string userId, Guid deliveryId, string? title, string body, string notificationType, DateTimeOffset now)
    {
        TenantId = tenantId;
        UserId = userId;
        DeliveryId = deliveryId;
        Title = title;
        Body = body;
        NotificationType = notificationType;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public string UserId { get; private set; } = default!;

    /// <summary>UNIQUE: a retried InApp delivery can never create two inbox rows (idempotent provider).</summary>
    public Guid DeliveryId { get; private set; }
    public string? Title { get; private set; }
    public string Body { get; private set; } = default!;
    public string NotificationType { get; private set; } = default!;
    public bool IsRead { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(DateTimeOffset now)
    {
        if (IsRead) return;
        IsRead = true;
        ReadAt = now;
    }
}
