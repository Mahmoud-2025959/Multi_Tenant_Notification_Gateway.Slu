using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Application.Inbox
{
    public sealed record InboxItemDto(Guid Id, Guid DeliveryId, string? Title, string Body, string NotificationType, bool IsRead, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    /// <summary>The InApp channel from the user's side: their own inbox only (tenant filter + user filter).</summary>
    public sealed class InboxService(IApplicationDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        public async Task<PagedResult<InboxItemDto>> ListAsync(bool unreadOnly, int page, int pageSize, CancellationToken ct)
        {
            var userId = UserId();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.InAppNotifications.AsNoTracking().Where(n => n.UserId == userId);
            if (unreadOnly) query = query.Where(n => !n.IsRead);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(n => n.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(n => new InboxItemDto(n.Id, n.DeliveryId, n.Title, n.Body, n.NotificationType, n.IsRead, n.CreatedAt, n.ReadAt))
                .ToListAsync(ct);
            return new PagedResult<InboxItemDto>(items, page, pageSize, total);
        }

        public Task<int> UnreadCountAsync(CancellationToken ct)
        {
            var userId = UserId();
            return db.InAppNotifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);
        }

        public async Task MarkReadAsync(Guid id, CancellationToken ct)
        {
            var userId = UserId();
            var item = await db.InAppNotifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct)
                       ?? throw new NotFoundException("InboxItem", id);
            item.MarkRead(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        public async Task<int> MarkAllReadAsync(CancellationToken ct)
        {
            var userId = UserId();
            var now = clock.GetUtcNow();
            var items = await db.InAppNotifications.Where(n => n.UserId == userId && !n.IsRead).Take(1000).ToListAsync(ct);
            foreach (var item in items) item.MarkRead(now);
            await db.SaveChangesAsync(ct);
            return items.Count;
        }

        private string UserId()
        {
            tenant.RequireTenantId();
            return tenant.UserId ?? throw new TenantAccessDeniedException("The token has no user id (sub).");
        }
    }
}

namespace NotificationGateway.Application.Reports
{
    using NotificationGateway.Application.Settings;

    /// <summary>Common filters (spec §5.H): date range, channel, provider, notification type, status.</summary>
    public sealed record ReportFilter(
        DateTimeOffset From, DateTimeOffset To, NotificationChannel? Channel = null, string? ProviderKey = null,
        string? NotificationType = null, DeliveryStatus? Status = null);

    public sealed record ChannelStatusCount(NotificationChannel Channel, DeliveryStatus Status, int Count);

    public sealed record DeliverySummaryReport(
        DateTimeOffset From, DateTimeOffset To, int TotalNotifications, int TotalDeliveries, double SuccessRate, double FailureRate,
        int DeadLettered, int Uncertain, int DeliveriesRetried, int TotalRetries, IReadOnlyList<ChannelStatusCount> ByChannelAndStatus,
        IReadOnlyList<QuotaDto> QuotaConsumption);

    public sealed record ProviderPerformanceRow(
        string ProviderKey, int Calls, int Accepted, int Failed, int Uncertain, int Skipped, double? AverageLatencyMs, long? MaxLatencyMs, double SuccessRate);

    public sealed record CostRow(string ProviderKey, int AcceptedMessages, decimal? CostPerMessage, string? Currency, decimal? EstimatedCost);

    public sealed record DailyVolumeRow(DateOnly Day, NotificationChannel Channel, int Count);

    public sealed record TenantUsageRow(Guid TenantId, string TenantCode, int Notifications, int Deliveries, int Delivered, int Failed, int DeadLettered);

    /// <summary>Tenant reports. Every query runs under the tenant query filter, so a tenant only ever sees its own rows.</summary>
    public sealed class ReportService(IApplicationDbContext db, ITenantContext tenant, QuotaSettingsService quotas)
    {
        private IQueryable<NotificationGateway.Domain.Notifications.NotificationDelivery> Deliveries(ReportFilter f)
        {
            var q = db.Deliveries.AsNoTracking().Where(d => d.CreatedAt >= f.From && d.CreatedAt < f.To);
            if (f.Channel is not null) q = q.Where(d => d.Channel == f.Channel);
            if (!string.IsNullOrWhiteSpace(f.ProviderKey)) q = q.Where(d => d.LastProviderKey == f.ProviderKey);
            if (!string.IsNullOrWhiteSpace(f.NotificationType)) q = q.Where(d => d.Notification.NotificationType == f.NotificationType);
            if (f.Status is not null) q = q.Where(d => d.Status == f.Status);
            return q;
        }

        private IQueryable<NotificationGateway.Domain.Notifications.DeliveryAttempt> Attempts(ReportFilter f)
        {
            var q = db.DeliveryAttempts.AsNoTracking().Where(a => a.StartedAt >= f.From && a.StartedAt < f.To);
            if (!string.IsNullOrWhiteSpace(f.ProviderKey)) q = q.Where(a => a.ProviderKey == f.ProviderKey);
            if (f.Channel is not null || !string.IsNullOrWhiteSpace(f.NotificationType) || f.Status is not null)
            {
                var ids = Deliveries(f).Select(d => d.Id);
                q = q.Where(a => ids.Contains(a.DeliveryId));
            }
            return q;
        }

        public async Task<DeliverySummaryReport> DeliverySummaryAsync(ReportFilter f, CancellationToken ct)
        {
            tenant.RequireTenantId();
            var deliveries = Deliveries(f);

            var rows = await deliveries
                .GroupBy(d => new { d.Channel, d.Status })
                .Select(g => new { g.Key.Channel, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct);
            var notifications = await deliveries.Select(d => d.NotificationId).Distinct().CountAsync(ct);
            var retried = await deliveries.CountAsync(d => d.AttemptCount > 1, ct);
            var totalRetries = await deliveries.Where(d => d.AttemptCount > 1).SumAsync(d => (int?)(d.AttemptCount - 1), ct) ?? 0;

            var list = rows.Select(r => new ChannelStatusCount(r.Channel, r.Status, r.Count)).OrderBy(r => r.Channel).ThenBy(r => r.Status).ToList();
            int Sum(params DeliveryStatus[] s) => list.Where(r => s.Contains(r.Status)).Sum(r => r.Count);

            var success = Sum(DeliveryStatus.Sent, DeliveryStatus.Delivered);
            var failed = Sum(DeliveryStatus.Failed, DeliveryStatus.DeadLettered);
            var finished = success + failed;

            return new DeliverySummaryReport(f.From, f.To, notifications, list.Sum(r => r.Count),
                finished == 0 ? 0 : Math.Round(100.0 * success / finished, 2),
                finished == 0 ? 0 : Math.Round(100.0 * failed / finished, 2),
                Sum(DeliveryStatus.DeadLettered), Sum(DeliveryStatus.Uncertain), retried, totalRetries, list,
                await quotas.ListAsync(ct));
        }

        public async Task<IReadOnlyList<ProviderPerformanceRow>> ProviderPerformanceAsync(ReportFilter f, CancellationToken ct)
        {
            tenant.RequireTenantId();
            var rows = await Attempts(f)
                .GroupBy(a => a.ProviderKey)
                .Select(g => new
                {
                    ProviderKey = g.Key,
                    Calls = g.Count(),
                    Accepted = g.Count(a => a.Outcome == AttemptOutcome.Accepted),
                    Failed = g.Count(a => a.Outcome == AttemptOutcome.TransientFailure || a.Outcome == AttemptOutcome.ProviderFailure || a.Outcome == AttemptOutcome.RecipientRejected),
                    Uncertain = g.Count(a => a.Outcome == AttemptOutcome.Uncertain),
                    Skipped = g.Count(a => a.Outcome == AttemptOutcome.Skipped),
                    AverageLatency = g.Average(a => (double?)a.DurationMs),
                    MaxLatency = g.Max(a => a.DurationMs)
                })
                .ToListAsync(ct);

            return rows.Select(r =>
            {
                var called = r.Calls - r.Skipped;
                return new ProviderPerformanceRow(r.ProviderKey, r.Calls, r.Accepted, r.Failed, r.Uncertain, r.Skipped,
                    r.AverageLatency is null ? null : Math.Round(r.AverageLatency.Value, 1), r.MaxLatency,
                    called == 0 ? 0 : Math.Round(100.0 * r.Accepted / called, 2));
            }).OrderByDescending(r => r.Calls).ToList();
        }

        public async Task<IReadOnlyList<CostRow>> CostEstimateAsync(ReportFilter f, CancellationToken ct)
        {
            tenant.RequireTenantId();
            var counts = await Attempts(f)
                .Where(a => a.Outcome == AttemptOutcome.Accepted)
                .GroupBy(a => a.ProviderKey)
                .Select(g => new { ProviderKey = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var catalog = await db.NotificationProviders.AsNoTracking().ToDictionaryAsync(p => p.Key, ct);

            return counts.Select(c =>
            {
                catalog.TryGetValue(c.ProviderKey, out var p);
                return new CostRow(c.ProviderKey, c.Count, p?.EstimatedCostPerMessage, p?.CostCurrency,
                    p?.EstimatedCostPerMessage is null ? null : p.EstimatedCostPerMessage * c.Count);
            }).OrderByDescending(c => c.EstimatedCost ?? 0).ToList();
        }

        public async Task<IReadOnlyList<DailyVolumeRow>> DailyVolumeAsync(ReportFilter f, CancellationToken ct)
        {
            tenant.RequireTenantId();
            var rows = await Deliveries(f).Select(d => new { d.CreatedAt, d.Channel }).ToListAsync(ct);
            return rows.GroupBy(r => new { Day = DateOnly.FromDateTime(r.CreatedAt.UtcDateTime), r.Channel })
                .Select(g => new DailyVolumeRow(g.Key.Day, g.Key.Channel, g.Count()))
                .OrderBy(r => r.Day).ThenBy(r => r.Channel).ToList();
        }
    }

    /// <summary>Platform-wide usage per tenant. SystemAdmin only (enforced by the controller policy AND here).</summary>
    public sealed class AdminReportService(IApplicationDbContext db, ITenantContext tenant)
    {
        public async Task<IReadOnlyList<TenantUsageRow>> TenantUsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            if (!tenant.IsSystemAdmin) throw new TenantAccessDeniedException("System administrators only.");
            using var systemScope = tenant.BeginSystemScope();

            var rows = await db.Deliveries.AsNoTracking()
                .Where(d => d.CreatedAt >= from && d.CreatedAt < to)
                .GroupBy(d => d.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Notifications = g.Select(d => d.NotificationId).Distinct().Count(),
                    Deliveries = g.Count(),
                    Delivered = g.Count(d => d.Status == DeliveryStatus.Delivered || d.Status == DeliveryStatus.Sent),
                    Failed = g.Count(d => d.Status == DeliveryStatus.Failed),
                    DeadLettered = g.Count(d => d.Status == DeliveryStatus.DeadLettered)
                })
                .ToListAsync(ct);
            var codes = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Code, ct);

            return rows.Select(r => new TenantUsageRow(r.TenantId, codes.GetValueOrDefault(r.TenantId, "?"), r.Notifications, r.Deliveries,
                r.Delivered, r.Failed, r.DeadLettered)).OrderByDescending(r => r.Deliveries).ToList();
        }
    }
}
