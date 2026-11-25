using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Routing;
using NotificationGateway.Application.Templates;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Outbox;
using NotificationGateway.Domain.Quotas;

namespace NotificationGateway.Application.Notifications;
/// <summary>Read side: notification history, delivery details with every attempt, problem deliveries (dead letters).</summary>
public sealed class NotificationQueryService(IApplicationDbContext db, ITenantContext tenant)
{
    public async Task<NotificationDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var n = await db.Notifications.AsNoTracking()
            .Include(x => x.Deliveries).ThenInclude(d => d.Recipient)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Notification", id);

        var deliveryIds = n.Deliveries.Select(d => d.Id).ToList();
        var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => deliveryIds.Contains(a.DeliveryId))
            .OrderBy(a => a.AttemptNumber).ToListAsync(ct);

        var deliveries = n.Deliveries.OrderBy(d => d.CreatedAt).Select(d => new DeliveryDetailDto(
            d.Id, d.RecipientId, d.Recipient?.UserId, d.Channel, d.Status, d.AttemptCount, d.MaxAttempts, d.LastProviderKey, d.ProviderMessageId,
            d.LastError, Mask(d.Channel, d.Address), d.Subject, d.Language, d.TemplateVersion, d.CreatedAt, d.SentAt, d.DeliveredAt, d.NextAttemptAt,
            attempts.Where(a => a.DeliveryId == d.Id).Select(a => new DeliveryAttemptDto(a.AttemptNumber, a.ProviderKey, a.ProviderConfigurationId,
                a.Outcome, a.ProviderMessageId, a.ErrorCode, a.ErrorMessage, a.HttpStatus, a.StartedAt, a.DurationMs)).ToList())).ToList();

        return new NotificationDetailDto(n.Id, n.NotificationType, n.TemplateCode, n.Priority, n.IdempotencyKey, n.CorrelationId, n.RequestedBy,
            n.CreatedAt, OverallStatus(n.Deliveries), deliveries);
    }

    public async Task<PagedResult<NotificationListItemDto>> ListAsync(NotificationQuery q, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        var query = db.Notifications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.NotificationType)) query = query.Where(n => n.NotificationType == q.NotificationType);
        if (q.From is not null) query = query.Where(n => n.CreatedAt >= q.From);
        if (q.To is not null) query = query.Where(n => n.CreatedAt < q.To);
        if (q.Status is not null) query = query.Where(n => n.Deliveries.Any(d => d.Status == q.Status));
        if (q.Channel is not null) query = query.Where(n => n.Deliveries.Any(d => d.Channel == q.Channel));
        if (!string.IsNullOrWhiteSpace(q.ProviderKey)) query = query.Where(n => n.Deliveries.Any(d => d.LastProviderKey == q.ProviderKey));
        if (q.Priority is not null) query = query.Where(n => n.Priority == q.Priority);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAt).Skip((page - 1) * size).Take(size)
            .Select(n => new NotificationListItemDto(n.Id, n.NotificationType, n.TemplateCode, n.Priority, n.CreatedAt,
                n.Deliveries.Count(),
                n.Deliveries.Count(d => d.Status == DeliveryStatus.Sent || d.Status == DeliveryStatus.Delivered),
                n.Deliveries.Count(d => d.Status == DeliveryStatus.Failed || d.Status == DeliveryStatus.DeadLettered),
                n.Deliveries.Count(d => d.Status == DeliveryStatus.Queued || d.Status == DeliveryStatus.Retrying
                                        || d.Status == DeliveryStatus.Processing || d.Status == DeliveryStatus.Accepted)))
            .ToListAsync(ct);

        return new PagedResult<NotificationListItemDto>(items, page, size, total);
    }

    public async Task<PagedResult<DeliveryDetailDto>> ListProblemDeliveriesAsync(int page, int pageSize, CancellationToken ct)
    {
        tenant.RequireTenantId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.Deliveries.AsNoTracking().Include(d => d.Recipient)
            .Where(d => d.Status == DeliveryStatus.DeadLettered || d.Status == DeliveryStatus.Uncertain || d.Status == DeliveryStatus.Failed);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<DeliveryDetailDto>(rows.Select(d => new DeliveryDetailDto(d.Id, d.RecipientId, d.Recipient?.UserId, d.Channel, d.Status,
            d.AttemptCount, d.MaxAttempts, d.LastProviderKey, d.ProviderMessageId, d.LastError, Mask(d.Channel, d.Address), d.Subject, d.Language,
            d.TemplateVersion, d.CreatedAt, d.SentAt, d.DeliveredAt, d.NextAttemptAt, Array.Empty<DeliveryAttemptDto>())).ToList(), page, pageSize, total);
    }

    internal static string OverallStatus(IEnumerable<NotificationDelivery> deliveries)
    {
        var list = deliveries.ToList();
        if (list.Count == 0) return "Empty";
        if (list.All(d => d.Status == DeliveryStatus.Delivered)) return "Delivered";
        if (list.Any(d => !d.IsTerminal && d.Status != DeliveryStatus.Sent && d.Status != DeliveryStatus.Uncertain)) return "InProgress";
        if (list.All(d => d.Status is DeliveryStatus.Failed or DeliveryStatus.DeadLettered or DeliveryStatus.Cancelled or DeliveryStatus.Suppressed))
            return "Failed";
        if (list.Any(d => d.Status is DeliveryStatus.Failed or DeliveryStatus.DeadLettered)) return "PartiallyDelivered";
        return "Sent";
    }

    /// <summary>Addresses are personal data: shown masked in API responses and logs.</summary>
    internal static string? Mask(NotificationChannel channel, string? address)
    {
        if (string.IsNullOrEmpty(address)) return address;
        if (channel is NotificationChannel.InApp or NotificationChannel.SignalR) return address;
        if (address.Length <= 4) return "****";
        var at = address.IndexOf('@');
        return at > 1 ? address[0] + new string('*', at - 1) + address[at..] : new string('*', address.Length - 4) + address[^4..];
    }
}
