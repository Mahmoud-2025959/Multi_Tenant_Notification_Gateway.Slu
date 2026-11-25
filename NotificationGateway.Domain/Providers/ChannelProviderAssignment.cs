using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Providers;

/// <summary>
/// Ordered provider chain for a tenant + channel. Order 0 = primary, 1.. = fallbacks.
/// May point to a tenant-owned OR a shared configuration (never another tenant's; validated in Application + tested by T08).
/// </summary>
public sealed class ChannelProviderAssignment : Entity, ITenantOwned
{
    private ChannelProviderAssignment() { }

    public ChannelProviderAssignment(Guid tenantId, NotificationChannel channel, Guid providerConfigurationId, int order)
    {
        if (order < 0) throw new DomainException("assignment.order", "Order must be >= 0.");
        TenantId = tenantId;
        Channel = channel;
        ProviderConfigurationId = providerConfigurationId;
        Order = order;
    }

    public Guid TenantId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public Guid ProviderConfigurationId { get; private set; }
    public TenantProviderConfiguration ProviderConfiguration { get; private set; } = default!;
    public int Order { get; private set; }
}
