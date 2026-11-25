using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Domain.Auditing;
using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Quotas;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Domain.Tenants;
using NotificationGateway.Domain.Webhooks;

namespace NotificationGateway.Infrastructure.Persistence;

/// <summary>
/// Captures configuration changes into AuditLogs inside the same SaveChanges (same transaction), so a config change
/// without an audit row is impossible. Any property whose name contains "Secret" is written as "***".
/// </summary>
internal sealed class AuditTrailBuilder(ITenantContext tenant, TimeProvider clock)
{
    private static readonly HashSet<Type> AuditedTypes = new()
    {
        typeof(Tenant),
        typeof(NotificationProvider),
        typeof(TenantProviderConfiguration),
        typeof(TenantChannelSetting),
        typeof(ChannelProviderAssignment),
        typeof(NotificationTemplate),
        typeof(NotificationRoutingRule),
        typeof(NotificationQuota),
        typeof(NotificationWebhook),
        typeof(NotificationUserPreference)
    };

    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.Ordinal) { "RowVersion", "UpdatedAt" };

    public void Capture(ChangeTracker changeTracker)
    {
        var now = clock.GetUtcNow();
        var logs = new List<AuditLog>();

        foreach (var entry in changeTracker.Entries())
        {
            if (!AuditedTypes.Contains(entry.Metadata.ClrType)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var changes = new Dictionary<string, object?>();
            foreach (var property in entry.Properties)
            {
                var name = property.Metadata.Name;
                if (IgnoredProperties.Contains(name)) continue;
                if (entry.State == EntityState.Modified && !property.IsModified) continue;

                var redact = name.Contains("Secret", StringComparison.OrdinalIgnoreCase);
                object? Show(object? v) => redact ? (v is null ? null : "***") : v;

                changes[name] = entry.State switch
                {
                    EntityState.Added => Show(property.CurrentValue),
                    EntityState.Deleted => Show(property.OriginalValue),
                    _ => new { from = Show(property.OriginalValue), to = Show(property.CurrentValue) }
                };
            }

            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            var tenantId = entry.Entity switch
            {
                ITenantOwned o => o.TenantId,
                IOptionallyTenantOwned o => o.TenantId,
                Tenant t => t.Id,
                _ => (Guid?)null
            };

            var key = string.Join(",", entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue?.ToString()));

            logs.Add(new AuditLog(
                tenantId,
                tenant.UserId,
                entry.State.ToString(),
                entry.Metadata.ClrType.Name,
                key,
                JsonSerializer.Serialize(changes),
                now));
        }

        if (logs.Count > 0)
            changeTracker.Context.Set<AuditLog>().AddRange(logs);
    }
}
