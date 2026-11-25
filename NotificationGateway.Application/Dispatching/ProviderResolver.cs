using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Application.Dispatching;

public sealed record ResolvedProvider(TenantProviderConfiguration Configuration, INotificationProvider Adapter);

/// <summary>
/// ProviderResolver: turns the tenant's provider chain for a channel (primary + fallbacks, ordered) into usable
/// (configuration, adapter) pairs. A configuration is skipped when it is inactive, its catalog entry is disabled by the
/// platform, or no adapter for it is compiled into this build. Adapters are looked up by key in the registry:
/// no switch statements, so adding a provider never touches this class.
/// Must run inside the delivery's tenant scope (the query filter returns the tenant's own + shared configurations only).
/// </summary>
public sealed class ProviderResolver(IApplicationDbContext db, IProviderRegistry registry)
{
    public async Task<IReadOnlyList<ResolvedProvider>> ResolveChainAsync(NotificationChannel channel, CancellationToken ct)
    {
        var chain = await db.ChannelProviderAssignments.AsNoTracking()
            .Where(a => a.Channel == channel)
            .OrderBy(a => a.Order)
            .Select(a => a.ProviderConfiguration)
            .ToListAsync(ct);

        var catalog = await db.NotificationProviders.AsNoTracking().ToDictionaryAsync(p => p.Key, ct);

        var result = new List<ResolvedProvider>();
        foreach (var config in chain)
        {
            if (!config.IsActive) continue;
            if (!catalog.TryGetValue(config.ProviderKey, out var entry) || !entry.IsEnabled) continue;
            if (!registry.TryGet(config.ProviderKey, out var adapter)) continue;
            result.Add(new ResolvedProvider(config, adapter));
        }
        return result;
    }
}
