using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Auditing;
using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Outbox;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Quotas;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Domain.Tenants;
using NotificationGateway.Domain.Webhooks;

namespace NotificationGateway.Infrastructure.Persistence;

/// <summary>
/// Tenant isolation is enforced in THREE layers here (defence in depth):
///  1. Global query filters on every tenant-owned type (reads).
///  2. A write guard in SaveChanges that refuses to insert/update/delete a row of another tenant, and refuses
///     tenant users touching shared (TenantId == null) rows.
///  3. Composite foreign keys (TenantId, ParentId) on the notification aggregate so the database itself rejects
///     a delivery/attempt pointing to another tenant's notification.
/// </summary>
public sealed class NotificationGatewayDbContext : DbContext, IApplicationDbContext
{
    private readonly ITenantContext _tenant;
    private readonly AuditTrailBuilder _audit;

    public NotificationGatewayDbContext(DbContextOptions<NotificationGatewayDbContext> options, ITenantContext tenant, TimeProvider clock)
        : base(options)
    {
        _tenant = tenant;
        _audit = new AuditTrailBuilder(tenant, clock);
    }

    // Referenced by the query filters. EF Core evaluates these per query (they are parameters, not constants).
    public Guid? CurrentTenantId => _tenant.TenantId;
    public bool BypassTenantFilter => _tenant.IsSystemScope;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<NotificationProvider> NotificationProviders => Set<NotificationProvider>();
    public DbSet<TenantProviderConfiguration> ProviderConfigurations => Set<TenantProviderConfiguration>();
    public DbSet<TenantChannelSetting> ChannelSettings => Set<TenantChannelSetting>();
    public DbSet<ChannelProviderAssignment> ChannelProviderAssignments => Set<ChannelProviderAssignment>();
    public DbSet<ProviderHealth> ProviderHealth => Set<ProviderHealth>();
    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();
    public DbSet<NotificationRoutingRule> RoutingRules => Set<NotificationRoutingRule>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRecipient> Recipients => Set<NotificationRecipient>();
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();
    public DbSet<NotificationUserPreference> UserPreferences => Set<NotificationUserPreference>();
    public DbSet<NotificationQuota> Quotas => Set<NotificationQuota>();
    public DbSet<QuotaUsage> QuotaUsages => Set<QuotaUsage>();
    public DbSet<NotificationWebhook> Webhooks => Set<NotificationWebhook>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationGatewayDbContext).Assembly);
        ApplyTenantFilters(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<string>().HaveMaxLength(256);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceTenantWriteGuard();
        _audit.Capture(ChangeTracker);
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        EnforceTenantWriteGuard();
        _audit.Capture(ChangeTracker);
        return base.SaveChanges();
    }

    // ------------------------------------------------------------------ isolation

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var owned = typeof(NotificationGatewayDbContext).GetMethod(nameof(SetTenantOwnedFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var optional = typeof(NotificationGatewayDbContext).GetMethod(nameof(SetOptionallyTenantOwnedFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.BaseType is null).ToList())
        {
            var clr = entityType.ClrType;
            if (typeof(ITenantOwned).IsAssignableFrom(clr))
                owned.MakeGenericMethod(clr).Invoke(this, new object[] { modelBuilder });
            else if (typeof(IOptionallyTenantOwned).IsAssignableFrom(clr))
                optional.MakeGenericMethod(clr).Invoke(this, new object[] { modelBuilder });
        }
    }

    private void SetTenantOwnedFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => BypassTenantFilter || e.TenantId == CurrentTenantId;
        modelBuilder.Entity<T>().HasQueryFilter(filter);
    }

    private void SetOptionallyTenantOwnedFilter<T>(ModelBuilder modelBuilder) where T : class, IOptionallyTenantOwned
    {
        // Tenants see their own rows + shared rows. Never another tenant's.
        Expression<Func<T, bool>> filter = e => BypassTenantFilter || e.TenantId == null || e.TenantId == CurrentTenantId;
        modelBuilder.Entity<T>().HasQueryFilter(filter);
    }

    private void EnforceTenantWriteGuard()
    {
        if (_tenant.IsSystemScope) return;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            switch (entry.Entity)
            {
                case ITenantOwned owned when owned.TenantId != _tenant.TenantId:
                    throw new TenantAccessDeniedException(
                        $"Refused to write {entry.Metadata.ClrType.Name} belonging to another tenant.");

                case IOptionallyTenantOwned optional when optional.TenantId is null && !_tenant.IsSystemAdmin:
                    throw new TenantAccessDeniedException(
                        $"Only system administrators can modify shared {entry.Metadata.ClrType.Name} rows.");

                case IOptionallyTenantOwned optional when optional.TenantId is not null && optional.TenantId != _tenant.TenantId:
                    throw new TenantAccessDeniedException(
                        $"Refused to write {entry.Metadata.ClrType.Name} belonging to another tenant.");
            }
        }
    }
}
