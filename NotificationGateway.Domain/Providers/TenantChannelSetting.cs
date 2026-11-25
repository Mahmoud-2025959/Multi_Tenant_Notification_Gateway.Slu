using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Providers;

/// <summary>
/// Per tenant, per channel: activation + retry/timeout policy.
/// A channel with no row is treated as DISABLED (secure default: nothing is sent until a tenant opts a channel in).
/// </summary>
public sealed class TenantChannelSetting : Entity, ITenantOwned
{
    private TenantChannelSetting() { }

    public TenantChannelSetting(Guid tenantId, NotificationChannel channel, bool isEnabled)
    {
        TenantId = tenantId;
        Channel = channel;
        IsEnabled = isEnabled;
    }

    public Guid TenantId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public bool IsEnabled { get; private set; }

    /// <summary>Total provider calls allowed for one delivery (across all providers in the chain) before dead-lettering.</summary>
    public int MaxAttempts { get; private set; } = 5;

    /// <summary>Exponential backoff base: delay = min(Base * 2^(round-1), Max) + jitter.</summary>
    public int BaseRetryDelaySeconds { get; private set; } = 10;
    public int MaxRetryDelaySeconds { get; private set; } = 900;

    /// <summary>Per provider call timeout.</summary>
    public int ProviderTimeoutSeconds { get; private set; } = 15;

    public void Update(bool isEnabled, int maxAttempts, int baseRetryDelaySeconds, int maxRetryDelaySeconds, int providerTimeoutSeconds)
    {
        if (maxAttempts is < 1 or > 20) throw new DomainException("channel.max_attempts", "MaxAttempts must be between 1 and 20.");
        if (baseRetryDelaySeconds < 1 || maxRetryDelaySeconds < baseRetryDelaySeconds)
            throw new DomainException("channel.retry_delay", "Retry delays are invalid.");
        if (providerTimeoutSeconds is < 1 or > 120) throw new DomainException("channel.timeout", "Timeout must be between 1 and 120 seconds.");

        IsEnabled = isEnabled;
        MaxAttempts = maxAttempts;
        BaseRetryDelaySeconds = baseRetryDelaySeconds;
        MaxRetryDelaySeconds = maxRetryDelaySeconds;
        ProviderTimeoutSeconds = providerTimeoutSeconds;
    }
}
