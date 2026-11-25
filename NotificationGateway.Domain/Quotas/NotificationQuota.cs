using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Quotas;

/// <summary>
/// Tenant quota: at most <see cref="Limit"/> deliveries per period, optionally per channel (Channel == null = all channels).
/// </summary>
public sealed class NotificationQuota : Entity, ITenantOwned
{
    private NotificationQuota() { }

    public NotificationQuota(Guid tenantId, NotificationChannel? channel, QuotaPeriod period, long limit)
    {
        if (limit < 0) throw new DomainException("quota.limit", "Quota limit must be >= 0.");
        TenantId = tenantId;
        Channel = channel;
        Period = period;
        Limit = limit;
    }

    public Guid TenantId { get; private set; }
    public NotificationChannel? Channel { get; private set; }
    public QuotaPeriod Period { get; private set; }
    public long Limit { get; private set; }

    public void UpdateLimit(long limit)
    {
        if (limit < 0) throw new DomainException("quota.limit", "Quota limit must be >= 0.");
        Limit = limit;
    }

    public static string PeriodKey(QuotaPeriod period, DateTimeOffset utcNow) => period switch
    {
        QuotaPeriod.Daily => utcNow.UtcDateTime.ToString("yyyy-MM-dd"),
        QuotaPeriod.Monthly => utcNow.UtcDateTime.ToString("yyyy-MM"),
        _ => throw new ArgumentOutOfRangeException(nameof(period))
    };
}
