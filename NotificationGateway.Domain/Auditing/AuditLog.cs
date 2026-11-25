namespace NotificationGateway.Domain.Auditing;

/// <summary>
/// Append-only audit of configuration changes (providers, channels, templates, routing, quotas, secrets).
/// Changes are captured automatically by the SaveChanges interceptor; secret columns are always written as "***".
/// TenantId is null for system-level changes (shared configurations, provider catalog).
/// </summary>
public sealed class AuditLog
{
    private AuditLog() { }

    public AuditLog(Guid? tenantId, string? userId, string action, string entityType, string entityId, string? changesJson, DateTimeOffset at)
    {
        TenantId = tenantId;
        UserId = userId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        ChangesJson = changesJson;
        At = at;
    }

    public long Id { get; private set; }
    public Guid? TenantId { get; private set; }
    public string? UserId { get; private set; }
    public string Action { get; private set; } = default!;
    public string EntityType { get; private set; } = default!;
    public string EntityId { get; private set; } = default!;
    public string? ChangesJson { get; private set; }
    public DateTimeOffset At { get; private set; }
}
