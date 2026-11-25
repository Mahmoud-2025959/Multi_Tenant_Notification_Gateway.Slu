using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationGateway.Domain.Auditing;
using NotificationGateway.Domain.Outbox;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Quotas;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Domain.Tenants;
using NotificationGateway.Domain.Webhooks;

namespace NotificationGateway.Infrastructure.Persistence.Configurations;

internal sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> b)
    {
        b.ToTable("NotificationTemplates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(128).IsRequired();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Language).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.Subject).HasMaxLength(512);
        b.Property(x => x.Body).HasColumnType("nvarchar(max)");
        b.Property(x => x.Placeholders).HasMaxLength(1000);
        b.Property(x => x.CreatedBy).HasMaxLength(128);
        b.Ignore(x => x.PlaceholderNames);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Versioning: each (owner, code, channel, language) has unique version numbers.
        // SQL Server treats NULL TenantId values as equal in a unique index, which is exactly right for shared templates.
        b.HasIndex(x => new { x.TenantId, x.Code, x.Channel, x.Language, x.Version }).IsUnique();
        b.HasIndex(x => new { x.Code, x.Channel, x.Language, x.IsActive });
    }
}

internal sealed class NotificationRoutingRuleConfiguration : IEntityTypeConfiguration<NotificationRoutingRule>
{
    public void Configure(EntityTypeBuilder<NotificationRoutingRule> b)
    {
        b.ToTable("NotificationRoutingRules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.NotificationType).HasMaxLength(128);
        b.Property(x => x.MinPriority).HasConversion<int?>();
        b.Property(x => x.Channels).HasMaxLength(256);
        b.Ignore(x => x.ChannelList);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.IsActive, x.Order });
    }
}

internal sealed class NotificationUserPreferenceConfiguration : IEntityTypeConfiguration<NotificationUserPreference>
{
    public void Configure(EntityTypeBuilder<NotificationUserPreference> b)
    {
        b.ToTable("NotificationUserPreferences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.UserId).HasMaxLength(128).IsRequired();
        b.Property(x => x.Language).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.DisabledChannels).HasMaxLength(256);
        b.Property(x => x.PreferredChannels).HasMaxLength(256).HasDefaultValue(string.Empty);
        b.Ignore(x => x.PreferredChannelSet);
        b.Property(x => x.DisabledNotificationTypes).HasMaxLength(2000);
        b.Property(x => x.TimeZoneId).HasMaxLength(64);
        b.Ignore(x => x.DisabledChannelSet);
        b.Ignore(x => x.DisabledTypeSet);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
    }
}

internal sealed class NotificationQuotaConfiguration : IEntityTypeConfiguration<NotificationQuota>
{
    public void Configure(EntityTypeBuilder<NotificationQuota> b)
    {
        b.ToTable("NotificationQuotas");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Period).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Channel, x.Period }).IsUnique();
    }
}

internal sealed class QuotaUsageConfiguration : IEntityTypeConfiguration<QuotaUsage>
{
    public void Configure(EntityTypeBuilder<QuotaUsage> b)
    {
        b.ToTable("NotificationQuotaUsages");
        b.HasKey(x => new { x.TenantId, x.ChannelKey, x.PeriodKey });
        b.Property(x => x.ChannelKey).HasMaxLength(16);
        b.Property(x => x.PeriodKey).HasMaxLength(10);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationWebhookConfiguration : IEntityTypeConfiguration<NotificationWebhook>
{
    public void Configure(EntityTypeBuilder<NotificationWebhook> b)
    {
        b.ToTable("NotificationWebhooks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ProviderKey).HasMaxLength(64);
        b.Property(x => x.PublicToken).HasMaxLength(64).IsRequired();
        b.Property(x => x.EncryptedSigningSecret).HasMaxLength(2000).IsRequired();
        b.Property(x => x.PreviousEncryptedSigningSecret).HasMaxLength(2000);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TenantProviderConfiguration>().WithMany().HasForeignKey(x => x.ProviderConfigurationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PublicToken).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ProviderConfigurationId });
    }
}

internal sealed class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> b)
    {
        b.ToTable("ProcessedWebhookEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderKey).HasMaxLength(64);
        b.Property(x => x.EventId).HasMaxLength(256);
        b.HasIndex(x => new { x.ProviderKey, x.EventId }).IsUnique();   // replay protection
        b.HasIndex(x => x.ReceivedAt);                                  // retention clean-up
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("OutboxMessages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasMaxLength(128);
        b.Property(x => x.Payload).HasColumnType("nvarchar(max)");
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => new { x.ProcessedAt, x.OccurredAt }).HasFilter("[ProcessedAt] IS NULL").HasDatabaseName("IX_Outbox_Pending");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(128);
        b.Property(x => x.Action).HasMaxLength(32);
        b.Property(x => x.EntityType).HasMaxLength(128);
        b.Property(x => x.EntityId).HasMaxLength(128);
        b.Property(x => x.ChangesJson).HasColumnType("nvarchar(max)");
        b.HasIndex(x => new { x.TenantId, x.At });
    }
}
