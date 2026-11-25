namespace NotificationGateway.Domain.Enums;

/// <summary>What to do when a tenant quota is exhausted.</summary>
public enum QuotaExceededPolicy
{
    /// <summary>Reject the API request with 429.</summary>
    Reject = 0,
    /// <summary>Accept but defer the delivery until the next period.</summary>
    Defer = 1
}
