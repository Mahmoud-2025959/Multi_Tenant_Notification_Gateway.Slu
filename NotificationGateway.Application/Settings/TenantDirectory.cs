using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Application.Settings;

/// <summary>
/// Resolves a client-supplied tenant reference ("tenant-001" code or GUID) to a tenant id.
/// The result is only ever COMPARED with the authenticated tenant; it never replaces it (spec §6:
/// "Never trust a client-supplied tenantId without validating it against the authenticated user's tenant").
/// </summary>
public sealed class TenantDirectory(IApplicationDbContext db)
{
    public async Task<Guid?> ResolveAsync(string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (Guid.TryParse(reference, out var id))
            return await db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);

        var code = reference.Trim().ToLowerInvariant();
        return await db.Tenants.AsNoTracking().Where(t => t.Code == code).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Throws 403 when the reference does not point at the caller's own tenant.</summary>
    public async Task EnsureIsCurrentAsync(string? reference, ITenantContext tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        var resolved = await ResolveAsync(reference, ct);
        if (resolved is null || resolved != tenant.TenantId)
            throw new TenantAccessDeniedException("The tenantId in the request does not match the authenticated tenant.");
    }
}

// ==================================================================== aggregate settings (spec §5.C)

public sealed record TenantPolicyDto(
    Guid TenantId, string Code, string Name, TemplateLanguage DefaultLanguage, string TimeZoneId,
    bool CriticalOverridesUserPreferences, QuotaExceededPolicy QuotaExceededPolicy, int RequestsPerMinute);

public sealed record ChannelWithChainDto(ChannelSettingDto Setting, IReadOnlyList<ProviderChainItemDto> Providers);

public sealed record NotificationSettingsDto(TenantPolicyDto Policy, IReadOnlyList<ChannelWithChainDto> Channels, IReadOnlyList<QuotaDto> Quotas);

/// <summary>What a TENANT admin may change. Rate limit and quota policy are commercial limits set by the platform.</summary>
public sealed record UpdateNotificationSettingsRequest(bool CriticalOverridesUserPreferences, TemplateLanguage DefaultLanguage, string TimeZoneId);

public sealed class TenantNotificationSettingsService(
    IApplicationDbContext db,
    ITenantContext tenant,
    ChannelSettingsService channels,
    ProviderConfigurationService providers,
    QuotaSettingsService quotas)
{
    public async Task<NotificationSettingsDto> GetAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tenantId, ct);

        var result = new List<ChannelWithChainDto>();
        foreach (var setting in await channels.ListAsync(ct))
            result.Add(new ChannelWithChainDto(setting, setting.IsConfigured ? await providers.GetChainAsync(setting.Channel, ct) : Array.Empty<ProviderChainItemDto>()));

        return new NotificationSettingsDto(
            new TenantPolicyDto(t.Id, t.Code, t.Name, t.DefaultLanguage, t.TimeZoneId, t.CriticalOverridesUserPreferences, t.QuotaExceededPolicy, t.RequestsPerMinute),
            result,
            await quotas.ListAsync(ct));
    }

    public async Task<NotificationSettingsDto> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken ct)
    {
        Guard.TimeZone(request.TimeZoneId, nameof(request.TimeZoneId));
        var tenantId = tenant.RequireTenantId();
        var t = await db.Tenants.FirstAsync(x => x.Id == tenantId, ct);
        t.UpdatePolicies(request.CriticalOverridesUserPreferences, t.QuotaExceededPolicy, t.RequestsPerMinute, request.DefaultLanguage, request.TimeZoneId);
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }
}
