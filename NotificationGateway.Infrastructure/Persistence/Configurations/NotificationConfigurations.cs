using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Tenants;

namespace NotificationGateway.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        // Alternate key used as the principal of composite (TenantId, NotificationId) foreign keys.
        b.HasAlternateKey(x => new { x.TenantId, x.Id });

        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.NotificationType).HasMaxLength(128).IsRequired();
        b.Property(x => x.TemplateCode).HasMaxLength(128).IsRequired();
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.DataJson).HasColumnType("nvarchar(max)");
        b.Property(x => x.CorrelationId).HasMaxLength(128);
        b.Property(x => x.RequestedBy).HasMaxLength(128);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Recipients).WithOne()
            .HasForeignKey(r => new { r.TenantId, r.NotificationId })
            .HasPrincipalKey(n => new { n.TenantId, n.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Deliveries).WithOne(d => d.Notification)
            .HasForeignKey(d => new { d.TenantId, d.NotificationId })
            .HasPrincipalKey(n => new { n.TenantId, n.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Recipients).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Deliveries).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Idempotency (T11): one key per tenant. Filtered so requests without a key are not constrained.
        b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.NotificationType, x.CreatedAt });
    }
}

internal sealed class NotificationRecipientConfiguration : IEntityTypeConfiguration<NotificationRecipient>
{
    public void Configure(EntityTypeBuilder<NotificationRecipient> b)
    {
        b.ToTable("NotificationRecipients");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.TenantId, x.Id });

        b.Property(x => x.UserId).HasMaxLength(128);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.DeviceToken).HasMaxLength(512);
        b.Property(x => x.Language).HasConversion<string>().HasMaxLength(8);

        b.HasIndex(x => new { x.TenantId, x.UserId });
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> b)
    {
        b.ToTable("NotificationDeliveries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.TenantId, x.Id });

        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();   // int: used by the leasing SQL and indexes
        b.Property(x => x.Language).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.LockedBy).HasMaxLength(128);
        b.Property(x => x.LastProviderKey).HasMaxLength(64);
        b.Property(x => x.ProviderMessageId).HasMaxLength(256);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Property(x => x.Address).HasMaxLength(512);
        b.Property(x => x.Subject).HasMaxLength(512);
        b.Property(x => x.Body).HasColumnType("nvarchar(max)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsTerminal);
        b.Ignore(x => x.AttemptsExhausted);
        b.Ignore(x => x.ProviderIdempotencyKey);

        // Restrict (NO ACTION): Notification -> Recipients and Notification -> Deliveries already cascade;
        // SQL Server rejects multiple cascade paths.
        b.HasOne(x => x.Recipient).WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RecipientId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One delivery per recipient per channel: the second line of defence against duplicate fan-out.
        b.HasIndex(x => new { x.NotificationId, x.RecipientId, x.Channel }).IsUnique();

        // Dispatcher: "due work" scan.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .IncludeProperties(x => new { x.TenantId, x.Priority })
            .HasDatabaseName("IX_Deliveries_Due");

        // Recovery: expired leases.
        b.HasIndex(x => new { x.Status, x.LockedUntil }).HasDatabaseName("IX_Deliveries_Lease");

        // Webhooks: match a provider callback to a delivery of the owning configuration.
        b.HasIndex(x => new { x.LastProviderConfigurationId, x.ProviderMessageId })
            .HasFilter("[ProviderMessageId] IS NOT NULL")
            .HasDatabaseName("IX_Deliveries_ProviderMessage");

        // Reports.
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IncludeProperties(x => new { x.Channel, x.Status, x.LastProviderKey });
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> b)
    {
        b.ToTable("NotificationDeliveryAttempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ProviderKey).HasMaxLength(64);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.ProviderMessageId).HasMaxLength(256);
        b.Property(x => x.ErrorCode).HasMaxLength(128);
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);

        b.HasOne<NotificationDelivery>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.DeliveryId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.DeliveryId, x.AttemptNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.StartedAt }).IncludeProperties(x => new { x.ProviderKey, x.Outcome, x.DurationMs });
    }
}

internal sealed class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public void Configure(EntityTypeBuilder<InAppNotification> b)
    {
        b.ToTable("InAppNotifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.UserId).HasMaxLength(128).IsRequired();
        b.Property(x => x.Title).HasMaxLength(512);
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.NotificationType).HasMaxLength(128);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Makes the InApp provider idempotent: a retry can never create a second inbox row.
        b.HasIndex(x => x.DeliveryId).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.UserId, x.IsRead, x.CreatedAt });
    }
}
