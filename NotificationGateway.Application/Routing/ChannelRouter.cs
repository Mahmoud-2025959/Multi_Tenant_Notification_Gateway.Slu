using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Application.Routing;

public sealed record RoutingDecision(
    IReadOnlyList<NotificationChannel> Channels,
    IReadOnlyList<NotificationChannel> DroppedBecauseDisabled,
    string? MatchedRule,
    IReadOnlyDictionary<NotificationChannel, TenantChannelSetting> Settings);

/// <summary>
/// Decides WHICH channels a notification uses (providers are chosen later, per channel, by the dispatcher).
///  1. First active routing rule (ascending Order) matching type + priority.
///  2. Rule allows caller override and the caller asked for channels → caller's channels; otherwise the rule's channels.
///  3. No rule → the caller's channels.
///  4. Tenant policy always wins: channels the tenant has not enabled are dropped.
/// </summary>
public sealed class ChannelRouter(IApplicationDbContext db)
{
    public async Task<RoutingDecision> RouteAsync(string notificationType, NotificationPriority priority,
        IReadOnlyList<NotificationChannel>? requestedChannels, CancellationToken ct)
    {
        var requested = (requestedChannels ?? Array.Empty<NotificationChannel>()).Distinct().ToList();

        var rules = await db.RoutingRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Order).ToListAsync(ct);
        var rule = rules.FirstOrDefault(r => r.Matches(notificationType, priority));

        IReadOnlyList<NotificationChannel> candidates = rule switch
        {
            null => requested,
            { AllowCallerOverride: true } when requested.Count > 0 => requested,
            _ => rule.ChannelList
        };

        var settings = await db.ChannelSettings.AsNoTracking().ToListAsync(ct);
        var enabled = settings.Where(s => s.IsEnabled).ToDictionary(s => s.Channel);

        return new RoutingDecision(
            candidates.Where(enabled.ContainsKey).ToList(),
            candidates.Where(c => !enabled.ContainsKey(c)).ToList(),
            rule?.Name,
            enabled);
    }
}
