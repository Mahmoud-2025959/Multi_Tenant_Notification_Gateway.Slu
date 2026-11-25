using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Tenants;

/// <summary>
/// A tenant is onboarded purely through data (this row + its configurations); no code changes.
/// </summary>
public sealed class Tenant : Entity
{
    private Tenant() { }

    public Tenant(string code, string name, TemplateLanguage defaultLanguage, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("tenant.code_required", "Tenant code is required.");
        Code = code.Trim().ToLowerInvariant();
        Name = name;
        DefaultLanguage = defaultLanguage;
        TimeZoneId = timeZoneId;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Public, human readable identifier (e.g. "tenant-001"). The Guid Id is used for all FKs.</summary>
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public TemplateLanguage DefaultLanguage { get; private set; }
    public string TimeZoneId { get; private set; } = "Africa/Cairo";

    /// <summary>When true, Critical notifications bypass user opt-outs and quiet hours (but never a disabled tenant channel).</summary>
    public bool CriticalOverridesUserPreferences { get; private set; } = true;

    /// <summary>Behaviour when a quota is exhausted.</summary>
    public QuotaExceededPolicy QuotaExceededPolicy { get; private set; } = QuotaExceededPolicy.Reject;

    /// <summary>Max requests per minute to the send API for this tenant (rate limit, separate from quotas).</summary>
    public int RequestsPerMinute { get; private set; } = 600;

    public DateTimeOffset CreatedAt { get; private set; }

    public void UpdatePolicies(bool criticalOverrides, QuotaExceededPolicy quotaPolicy, int requestsPerMinute, TemplateLanguage defaultLanguage, string timeZoneId)
    {
        if (requestsPerMinute <= 0) throw new DomainException("tenant.rate_limit_invalid", "RequestsPerMinute must be positive.");
        CriticalOverridesUserPreferences = criticalOverrides;
        QuotaExceededPolicy = quotaPolicy;
        RequestsPerMinute = requestsPerMinute;
        DefaultLanguage = defaultLanguage;
        TimeZoneId = timeZoneId;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}
