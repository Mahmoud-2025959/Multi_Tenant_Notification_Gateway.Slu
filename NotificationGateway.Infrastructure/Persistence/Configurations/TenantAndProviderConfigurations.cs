using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Tenants;

namespace NotificationGateway.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.TimeZoneId).HasMaxLength(64);
        b.Property(x => x.DefaultLanguage).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.QuotaExceededPolicy).HasConversion<string>().HasMaxLength(16);
    }
}

internal sealed class NotificationProviderConfiguration : IEntityTypeConfiguration<NotificationProvider>
{
    public void Configure(EntityTypeBuilder<NotificationProvider> b)
    {
        b.ToTable("NotificationProviders");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.DisplayName).HasMaxLength(128);
        b.Property(x => x.CostCurrency).HasMaxLength(3);
    }
}

internal sealed class TenantProviderConfigurationConfiguration : IEntityTypeConfiguration<TenantProviderConfiguration>
{
    public void Configure(EntityTypeBuilder<TenantProviderConfiguration> b)
    {
        b.ToTable("TenantProviderConfigurations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.SettingsJson).HasMaxLength(4000);
        b.Property(x => x.EncryptedSecrets).HasMaxLength(4000).IsRequired();
        b.Property(x => x.SenderIdentity).HasMaxLength(256);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsShared);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NotificationProvider>().WithMany().HasForeignKey(x => x.ProviderKey).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Channel, x.IsActive });
    }
}

internal sealed class TenantChannelSettingConfiguration : IEntityTypeConfiguration<TenantChannelSetting>
{
    public void Configure(EntityTypeBuilder<TenantChannelSetting> b)
    {
        b.ToTable("TenantChannelSettings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Channel }).IsUnique();
    }
}

internal sealed class ChannelProviderAssignmentConfiguration : IEntityTypeConfiguration<ChannelProviderAssignment>
{
    public void Configure(EntityTypeBuilder<ChannelProviderAssignment> b)
    {
        b.ToTable("ChannelProviderAssignments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ProviderConfiguration).WithMany().HasForeignKey(x => x.ProviderConfigurationId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Channel, x.Order }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Channel, x.ProviderConfigurationId }).IsUnique();
    }
}

internal sealed class ProviderHealthConfiguration : IEntityTypeConfiguration<ProviderHealth>
{
    public void Configure(EntityTypeBuilder<ProviderHealth> b)
    {
        b.ToTable("ProviderHealth");
        b.HasKey(x => x.ProviderConfigurationId);
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.HasOne<TenantProviderConfiguration>().WithOne().HasForeignKey<ProviderHealth>(x => x.ProviderConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
