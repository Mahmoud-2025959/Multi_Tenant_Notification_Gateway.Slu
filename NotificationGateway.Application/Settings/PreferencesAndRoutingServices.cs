using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Quotas;
using NotificationGateway.Domain.Routing;

namespace NotificationGateway.Application.Settings;

// ==================================================================== user preferences

public sealed record UserPreferenceDto(
    string UserId, TemplateLanguage? Language, IReadOnlyCollection<NotificationChannel> PreferredChannels, IReadOnlyCollection<NotificationChannel> DisabledChannels,
    IReadOnlyCollection<string> DisabledNotificationTypes, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd,
    string? TimeZoneId, bool AllowCriticalOverride);

public sealed record UpdateUserPreferenceRequest(
    TemplateLanguage? Language, IReadOnlyList<NotificationChannel>? DisabledChannels, IReadOnlyList<string>? DisabledNotificationTypes,
    TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, string? TimeZoneId, bool AllowCriticalOverride = true,
    IReadOnlyList<NotificationChannel>? PreferredChannels = null);

/// <summary>The calling user's own preferences inside the calling tenant.</summary>
public sealed class UserPreferenceService(IApplicationDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<UserPreferenceDto> GetMineAsync(CancellationToken ct)
    {
        var (tenantId, userId) = Caller();
        var pref = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct)
                   ?? new NotificationUserPreference(tenantId, userId);
        return ToDto(pref);
    }

    public async Task<UserPreferenceDto> UpdateMineAsync(UpdateUserPreferenceRequest request, CancellationToken ct)
    {
        Guard.TimeZone(request.TimeZoneId, nameof(request.TimeZoneId));
        var (tenantId, userId) = Caller();
        var pref = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (pref is null)
        {
            pref = new NotificationUserPreference(tenantId, userId);
            db.UserPreferences.Add(pref);
        }

        pref.Update(request.Language, request.DisabledChannels ?? Array.Empty<NotificationChannel>(),
            request.DisabledNotificationTypes ?? Array.Empty<string>(), request.QuietHoursStart, request.QuietHoursEnd,
            request.TimeZoneId, request.AllowCriticalOverride, clock.GetUtcNow(), request.PreferredChannels);
        await db.SaveChangesAsync(ct);
        return ToDto(pref);
    }

    private (Guid TenantId, string UserId) Caller()
    {
        var tenantId = tenant.RequireTenantId();
        var userId = tenant.UserId ?? throw new TenantAccessDeniedException("The token has no user id (sub).");
        return (tenantId, userId);
    }

    private static UserPreferenceDto ToDto(NotificationUserPreference p) => new(p.UserId, p.Language, p.PreferredChannelSet.ToList(), p.DisabledChannelSet.ToList(),
        p.DisabledTypeSet.ToList(), p.QuietHoursStart, p.QuietHoursEnd, p.TimeZoneId, p.AllowCriticalOverride);
}

// ==================================================================== quotas

public sealed record QuotaDto(Guid Id, NotificationChannel? Channel, QuotaPeriod Period, long Limit, long UsedInCurrentPeriod, string PeriodKey);

public sealed record UpsertQuotaRequest(NotificationChannel? Channel, QuotaPeriod Period, long Limit);

public sealed class QuotaSettingsService(IApplicationDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<IReadOnlyList<QuotaDto>> ListAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var now = clock.GetUtcNow();
        var quotas = await db.Quotas.AsNoTracking().ToListAsync(ct);
        var usages = await db.QuotaUsages.AsNoTracking().ToListAsync(ct);

        return quotas.Select(q =>
        {
            var periodKey = NotificationQuota.PeriodKey(q.Period, now);
            var channelKey = q.Channel?.ToString() ?? "*";
            var used = usages.FirstOrDefault(u => u.ChannelKey == channelKey && u.PeriodKey == periodKey)?.Used ?? 0;
            return new QuotaDto(q.Id, q.Channel, q.Period, q.Limit, used, periodKey);
        }).ToList();
    }

    public async Task<QuotaDto> UpsertAsync(UpsertQuotaRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var quota = await db.Quotas.FirstOrDefaultAsync(q => q.Channel == request.Channel && q.Period == request.Period, ct);
        if (quota is null)
        {
            quota = new NotificationQuota(tenantId, request.Channel, request.Period, request.Limit);
            db.Quotas.Add(quota);
        }
        else
        {
            quota.UpdateLimit(request.Limit);
        }
        await db.SaveChangesAsync(ct);
        return new QuotaDto(quota.Id, quota.Channel, quota.Period, quota.Limit, 0, NotificationQuota.PeriodKey(quota.Period, clock.GetUtcNow()));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var quota = await db.Quotas.FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new NotFoundException("Quota", id);
        db.Quotas.Remove(quota);
        await db.SaveChangesAsync(ct);
    }
}

// ==================================================================== routing rules

public sealed record RoutingRuleDto(
    Guid Id, string Name, string? NotificationType, NotificationPriority? MinPriority, IReadOnlyList<NotificationChannel> Channels,
    int Order, bool AllowCallerOverride, bool IsActive);

public sealed record UpsertRoutingRuleRequest(
    string Name, string? NotificationType, NotificationPriority? MinPriority, IReadOnlyList<NotificationChannel> Channels,
    int Order, bool AllowCallerOverride = true, bool IsActive = true);

public sealed class RoutingRuleService(IApplicationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<RoutingRuleDto>> ListAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        return (await db.RoutingRules.AsNoTracking().OrderBy(r => r.Order).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<RoutingRuleDto> CreateAsync(UpsertRoutingRuleRequest request, CancellationToken ct)
    {
        Guard.Required(request.Name, nameof(request.Name));
        var rule = new NotificationRoutingRule(tenant.RequireTenantId(), request.Name.Trim(), request.NotificationType, request.MinPriority,
            request.Channels ?? Array.Empty<NotificationChannel>(), request.Order, request.AllowCallerOverride);
        if (!request.IsActive)
            rule.Update(rule.Name, rule.NotificationType, rule.MinPriority, rule.ChannelList, rule.Order, rule.AllowCallerOverride, false);
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task<RoutingRuleDto> UpdateAsync(Guid id, UpsertRoutingRuleRequest request, CancellationToken ct)
    {
        Guard.Required(request.Name, nameof(request.Name));
        var rule = await db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("RoutingRule", id);
        rule.Update(request.Name.Trim(), request.NotificationType, request.MinPriority, request.Channels ?? Array.Empty<NotificationChannel>(),
            request.Order, request.AllowCallerOverride, request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("RoutingRule", id);
        db.RoutingRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    private static RoutingRuleDto ToDto(NotificationRoutingRule r) =>
        new(r.Id, r.Name, r.NotificationType, r.MinPriority, r.ChannelList, r.Order, r.AllowCallerOverride, r.IsActive);
}
