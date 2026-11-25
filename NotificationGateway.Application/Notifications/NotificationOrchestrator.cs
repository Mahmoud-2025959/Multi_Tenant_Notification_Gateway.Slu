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
/// <summary>
/// NotificationOrchestrator: the accept path (POST /api/notifications/send) plus cancel / requeue commands.
/// Everything in SendAsync happens in ONE transaction: idempotency check, routing (ChannelRouter), template rendering
/// (TemplateResolver + TemplateRenderer, content is snapshotted), quota consumption, notification + deliveries, outbox row.
/// Providers are never called here: the API returns 202 and the NotificationDispatcher (DeliveryProcessor) delivers
/// asynchronously, so a slow SMS vendor can never slow down the calling module.
/// </summary>
public sealed class NotificationOrchestrator(
    IApplicationDbContext db,
    ITenantContext tenant,
    ChannelRouter router,
    TemplateResolver templates,
    IQuotaCounter quotaCounter,
    IUnitOfWork unitOfWork,
    IDispatchSignal dispatchSignal,
    TimeProvider clock)
{
    public const int MaxRecipientsPerRequest = 1000;
    public const string OutboxTypeDeliveriesQueued = "deliveries.queued";

    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false };

    public async Task<SendNotificationResponse> SendAsync(SendNotificationRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        request = Normalize(request);
        Validate(request);

        var requestHash = Hash(request);
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();

        // ---- idempotency (T11): same key + same payload => same result; same key + different payload => 409
        if (idempotencyKey is not null)
        {
            var existing = await db.Notifications.AsNoTracking()
                .Include(n => n.Deliveries).ThenInclude(d => d.Recipient)
                .FirstOrDefaultAsync(n => n.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash)
                    throw new ConflictException("idempotency.key_reused", "This Idempotency-Key was already used with a different request body.");
                return ToResponse(existing, replayed: true, Array.Empty<string>());
            }
        }

        var tenantRow = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                        ?? throw new TenantAccessDeniedException("Unknown tenant.");
        var now = clock.GetUtcNow();
        var warnings = new List<string>();

        // ---- routing (which channels)
        var route = await router.RouteAsync(request.NotificationType, request.Priority, request.Channels, ct);
        foreach (var dropped in route.DroppedBecauseDisabled)
            warnings.Add($"Channel {dropped} is not enabled for this tenant and was skipped.");
        if (route.Channels.Count == 0)
            throw RequestValidationException.For("Channels", "No enabled channel matches this notification (check routing rules and channel settings).");

        // ---- recipients' language preferences (one query)
        var userIds = request.Recipients!.Where(r => r.UserId != null).Select(r => r.UserId!).Distinct().ToList();
        var preferences = await db.UserPreferences.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);

        var data = request.Data ?? JsonDocument.Parse("{}").RootElement;
        var notification = new Notification(tenantId, idempotencyKey, request.NotificationType, request.TemplateCode, request.Priority,
            data.GetRawText(), request.CorrelationId, tenant.UserId, now);
        notification.SetRequestHash(requestHash);

        var templateCache = new Dictionary<(NotificationChannel, TemplateLanguage), Domain.Templates.NotificationTemplate?>();

        foreach (var r in request.Recipients!)
        {
            var language = r.Language
                           ?? (r.UserId is not null && preferences.TryGetValue(r.UserId, out var pref) ? pref.Language : null)
                           ?? tenantRow.DefaultLanguage;

            var recipient = notification.AddRecipient(r.UserId, r.Email, r.Phone, r.DeviceToken, language);

            foreach (var channel in route.Channels)
            {
                var address = recipient.AddressFor(channel);
                if (string.IsNullOrWhiteSpace(address))
                {
                    warnings.Add($"Recipient {Describe(r)} has no address for {channel}; skipped.");
                    continue;
                }

                if (!templateCache.TryGetValue((channel, language), out var template))
                {
                    template = await templates.ResolveAsync(tenantId, request.TemplateCode, channel, language, tenantRow.DefaultLanguage, ct);
                    templateCache[(channel, language)] = template;
                }
                if (template is null)
                    throw RequestValidationException.For("TemplateCode",
                        $"No active template '{request.TemplateCode}' for channel {channel} (language {language} or {tenantRow.DefaultLanguage}).");

                var rendered = TemplateRenderer.Render(template, data, htmlEncodeValues: channel == NotificationChannel.Email);
                var setting = route.Settings[channel];
                var delivery = notification.AddDelivery(recipient, channel, setting.MaxAttempts, now);
                delivery.SetRenderedContent(template.Id, template.Version, template.Language, address, rendered.Subject, rendered.Body);
            }
        }

        if (notification.Deliveries.Count == 0)
            throw RequestValidationException.For("Recipients", "None of the recipients can be reached on the selected channels.");

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await ApplyQuotasAsync(tenantRow.QuotaExceededPolicy, notification, now, warnings, token);

            db.Notifications.Add(notification);
            db.OutboxMessages.Add(new OutboxMessage(tenantId, OutboxTypeDeliveriesQueued,
                JsonSerializer.Serialize(new { notificationId = notification.Id, deliveryIds = notification.Deliveries.Select(d => d.Id) }), now));

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException) when (idempotencyKey is not null)
            {
                // Two concurrent requests with the same key: the unique index let only one win.
                throw new ConflictException("idempotency.concurrent", "A request with this Idempotency-Key is being processed. Retry to get its result.");
            }
            return true;
        }, ct);

        dispatchSignal.Notify();
        return ToResponse(notification, replayed: false, warnings);
    }

    /// <summary>
    /// Quotas count DELIVERIES (what providers bill), per channel and for "all channels". Consumed atomically inside the
    /// transaction. Reject → 429 for the whole request; Defer → deliveries over quota wait for the next period.
    /// </summary>
    private async Task ApplyQuotasAsync(QuotaExceededPolicy policy, Notification notification, DateTimeOffset now, List<string> warnings, CancellationToken ct)
    {
        var quotas = await db.Quotas.AsNoTracking().ToListAsync(ct);
        if (quotas.Count == 0) return;

        foreach (var quota in quotas)
        {
            var affected = notification.Deliveries.Where(d => quota.Channel is null || d.Channel == quota.Channel).ToList();
            if (affected.Count == 0) continue;

            var ok = await quotaCounter.TryConsumeAsync(notification.TenantId, quota.Channel?.ToString() ?? "*",
                NotificationQuota.PeriodKey(quota.Period, now), affected.Count, quota.Limit, ct);
            if (ok) continue;

            var scope = quota.Channel?.ToString() ?? "all channels";
            if (policy == QuotaExceededPolicy.Reject)
                throw new QuotaExceededException($"{quota.Period} quota for {scope} ({quota.Limit}) would be exceeded by {affected.Count} deliveries.");

            var nextPeriod = NextPeriodStart(quota.Period, now);
            foreach (var delivery in affected)
                delivery.Defer(nextPeriod, $"Deferred: {quota.Period} quota for {scope} exhausted.", now);
            warnings.Add($"{quota.Period} quota for {scope} exhausted; {affected.Count} deliveries deferred until {nextPeriod:u}.");
        }
    }

    private static DateTimeOffset NextPeriodStart(QuotaPeriod period, DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return period == QuotaPeriod.Daily
            ? new DateTimeOffset(utc.Date.AddDays(1), TimeSpan.Zero)
            : new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1), TimeSpan.Zero);
    }

    /// <summary>Cancels deliveries that have not been handed to a provider yet.</summary>
    public async Task<int> CancelAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var n = await db.Notifications.Include(x => x.Deliveries).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Notification", id);
        var now = clock.GetUtcNow();
        var cancelled = n.Deliveries.Count(d => d.TryCancel(now));
        await db.SaveChangesAsync(ct);
        return cancelled;
    }

    public async Task RequeueAsync(Guid deliveryId, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct) ?? throw new NotFoundException("Delivery", deliveryId);
        var now = clock.GetUtcNow();
        delivery.Requeue(now);
        db.OutboxMessages.Add(new OutboxMessage(tenantId, OutboxTypeDeliveriesQueued,
            JsonSerializer.Serialize(new { notificationId = delivery.NotificationId, deliveryIds = new[] { delivery.Id } }), now));
        await db.SaveChangesAsync(ct);
        dispatchSignal.Notify();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Accepts both the spec's single "recipient" object and the "recipients" array.</summary>
    private static SendNotificationRequest Normalize(SendNotificationRequest r)
    {
        if (r.Recipient is null) return r with { Recipients = r.Recipients ?? Array.Empty<RecipientRequest>() };
        var all = (r.Recipients ?? Array.Empty<RecipientRequest>()).Append(r.Recipient).ToList();
        return r with { Recipients = all, Recipient = null };
    }

    private static void Validate(SendNotificationRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.NotificationType)) errors["NotificationType"] = new[] { "NotificationType is required." };
        else if (r.NotificationType.Length > 128) errors["NotificationType"] = new[] { "NotificationType is too long." };
        if (string.IsNullOrWhiteSpace(r.TemplateCode)) errors["TemplateCode"] = new[] { "TemplateCode is required." };
        if (r.Recipients is null || r.Recipients.Count == 0) errors["Recipients"] = new[] { "At least one recipient is required." };
        else if (r.Recipients.Count > MaxRecipientsPerRequest) errors["Recipients"] = new[] { $"At most {MaxRecipientsPerRequest} recipients per request." };
        else if (r.Recipients.Any(x => x.UserId is null && x.Email is null && x.Phone is null && x.DeviceToken is null))
            errors["Recipients"] = new[] { "Every recipient needs a userId, email, phone or deviceToken." };
        if (r.IdempotencyKey is { Length: > 128 }) errors["IdempotencyKey"] = new[] { "IdempotencyKey is too long." };
        if (r.Data is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            errors["Data"] = new[] { "Data must be a JSON object." };
        if (errors.Count > 0) throw new RequestValidationException(errors);
    }

    /// <summary>SHA-256 over the canonical request WITHOUT the idempotency key.</summary>
    private static string Hash(SendNotificationRequest r)
    {
        var canonical = JsonSerializer.Serialize(r with { IdempotencyKey = null, TenantId = null }, HashJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static SendNotificationResponse ToResponse(Notification n, bool replayed, IReadOnlyList<string> warnings) =>
        new(n.Id, replayed, n.Deliveries.Select(d => new DeliverySummaryDto(d.Id, d.Recipient?.UserId, d.Channel, d.Status, d.NextAttemptAt)).ToList(), warnings);

    private static string Describe(RecipientRequest r) => r.UserId ?? r.Email ?? r.Phone ?? "(device)";
}
