using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Notifications;

/// <summary>
/// The logical notification requested by a business module. It fans out into one
/// <see cref="NotificationDelivery"/> per (recipient, channel). Status is never stored here:
/// it is derived from the deliveries so there is a single source of truth.
/// </summary>
public sealed class Notification : Entity, ITenantOwned
{
    private readonly List<NotificationRecipient> _recipients = new();
    private readonly List<NotificationDelivery> _deliveries = new();

    private Notification() { }

    public Notification(
        Guid tenantId,
        string? idempotencyKey,
        string notificationType,
        string templateCode,
        NotificationPriority priority,
        string dataJson,
        string? correlationId,
        string? requestedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(notificationType)) throw new DomainException("notification.type_required", "Notification type is required.");
        if (string.IsNullOrWhiteSpace(templateCode)) throw new DomainException("notification.template_required", "Template code is required.");

        TenantId = tenantId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        NotificationType = notificationType.Trim();
        TemplateCode = templateCode.Trim().ToUpperInvariant();
        Priority = priority;
        DataJson = dataJson;
        CorrelationId = correlationId;
        RequestedBy = requestedBy;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Caller supplied key; UNIQUE per tenant. A replay returns the original notification instead of creating a new one (T11).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>SHA-256 of the canonical request. Same key + different payload => 409 instead of silently returning the old result.</summary>
    public string? RequestHash { get; private set; }

    public string NotificationType { get; private set; } = default!;
    public string TemplateCode { get; private set; } = default!;
    public NotificationPriority Priority { get; private set; }

    /// <summary>Placeholder values supplied by the caller (JSON object).</summary>
    public string DataJson { get; private set; } = "{}";
    public string? CorrelationId { get; private set; }
    public string? RequestedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<NotificationRecipient> Recipients => _recipients;
    public IReadOnlyCollection<NotificationDelivery> Deliveries => _deliveries;

    public void SetRequestHash(string hash) => RequestHash = hash;

    public NotificationRecipient AddRecipient(string? userId, string? email, string? phone, string? deviceToken, TemplateLanguage? language)
    {
        var recipient = new NotificationRecipient(TenantId, Id, userId, email, phone, deviceToken, language);
        _recipients.Add(recipient);
        return recipient;
    }

    public NotificationDelivery AddDelivery(NotificationRecipient recipient, NotificationChannel channel, int maxAttempts, DateTimeOffset now)
    {
        if (_deliveries.Any(d => d.RecipientId == recipient.Id && d.Channel == channel))
            throw new DomainException("notification.duplicate_delivery", $"Channel {channel} already added for this recipient.");

        var delivery = new NotificationDelivery(TenantId, Id, recipient.Id, channel, Priority, maxAttempts, now);
        _deliveries.Add(delivery);
        return delivery;
    }
}
