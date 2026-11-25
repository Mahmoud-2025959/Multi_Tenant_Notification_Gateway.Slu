using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Application.Settings;

public sealed record ChannelSettingDto(
    NotificationChannel Channel, bool IsEnabled, int MaxAttempts, int BaseRetryDelaySeconds, int MaxRetryDelaySeconds,
    int ProviderTimeoutSeconds, bool IsConfigured);

public sealed record UpdateChannelSettingRequest(
    bool IsEnabled, int MaxAttempts = 5, int BaseRetryDelaySeconds = 10, int MaxRetryDelaySeconds = 900, int ProviderTimeoutSeconds = 15);

/// <summary>Per-tenant channel activation + retry/timeout policy. A channel without a row is disabled.</summary>
public sealed class ChannelSettingsService(IApplicationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<ChannelSettingDto>> ListAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var rows = await db.ChannelSettings.AsNoTracking().ToListAsync(ct);
        return Enum.GetValues<NotificationChannel>()
            .Select(channel => rows.FirstOrDefault(r => r.Channel == channel) is { } s
                ? ToDto(s)
                : new ChannelSettingDto(channel, false, 5, 10, 900, 15, false))
            .ToList();
    }

    public async Task<ChannelSettingDto> UpsertAsync(NotificationChannel channel, UpdateChannelSettingRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var setting = await db.ChannelSettings.FirstOrDefaultAsync(s => s.Channel == channel, ct);
        if (setting is null)
        {
            setting = new TenantChannelSetting(tenantId, channel, request.IsEnabled);
            db.ChannelSettings.Add(setting);
        }

        setting.Update(request.IsEnabled, request.MaxAttempts, request.BaseRetryDelaySeconds, request.MaxRetryDelaySeconds, request.ProviderTimeoutSeconds);
        await db.SaveChangesAsync(ct);
        return ToDto(setting);
    }

    private static ChannelSettingDto ToDto(TenantChannelSetting s) =>
        new(s.Channel, s.IsEnabled, s.MaxAttempts, s.BaseRetryDelaySeconds, s.MaxRetryDelaySeconds, s.ProviderTimeoutSeconds, true);
}
