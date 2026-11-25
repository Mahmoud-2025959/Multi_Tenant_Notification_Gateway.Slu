using NotificationGateway.Domain.Common;

namespace NotificationGateway.Domain.Quotas;

/// <summary>
/// Counter row per (tenant, channel-or-all, period key). Incremented with a single conditional UPDATE
/// ("SET Used = Used + n WHERE Used + n &lt;= Limit") so concurrent API instances cannot overshoot the quota.
/// ChannelKey is the channel name or "*" (avoids a nullable column in the unique key).
/// </summary>
public sealed class QuotaUsage : ITenantOwned
{
    private QuotaUsage() { }

    public QuotaUsage(Guid tenantId, string channelKey, string periodKey, long used)
    {
        TenantId = tenantId;
        ChannelKey = channelKey;
        PeriodKey = periodKey;
        Used = used;
    }

    public Guid TenantId { get; private set; }
    public string ChannelKey { get; private set; } = default!;
    public string PeriodKey { get; private set; } = default!;
    public long Used { get; private set; }
}
