using Microsoft.EntityFrameworkCore;
using NotificationGateway.Domain.Auditing;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Outbox;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Quotas;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Domain.Tenants;
using NotificationGateway.Domain.Webhooks;

namespace NotificationGateway.Application.Abstractions;

/// <summary>
/// Trade-off (documented in the architecture doc): Application depends on EF Core abstractions (DbSet/IQueryable)
/// instead of hand written repositories. It keeps queries expressive and testable against SQL Server via Testcontainers,
/// while provider SDKs, SQL specifics (leasing, quota counters) and encryption stay in Infrastructure.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<NotificationProvider> NotificationProviders { get; }
    DbSet<TenantProviderConfiguration> ProviderConfigurations { get; }
    DbSet<TenantChannelSetting> ChannelSettings { get; }
    DbSet<ChannelProviderAssignment> ChannelProviderAssignments { get; }
    DbSet<ProviderHealth> ProviderHealth { get; }
    DbSet<NotificationTemplate> Templates { get; }
    DbSet<NotificationRoutingRule> RoutingRules { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<NotificationRecipient> Recipients { get; }
    DbSet<NotificationDelivery> Deliveries { get; }
    DbSet<DeliveryAttempt> DeliveryAttempts { get; }
    DbSet<InAppNotification> InAppNotifications { get; }
    DbSet<NotificationUserPreference> UserPreferences { get; }
    DbSet<NotificationQuota> Quotas { get; }
    DbSet<QuotaUsage> QuotaUsages { get; }
    DbSet<NotificationWebhook> Webhooks { get; }
    DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
