namespace NotificationGateway.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

/// <summary>
/// Marks an entity that ALWAYS belongs to exactly one tenant.
/// Persistence applies a global query filter on TenantId for every type implementing this,
/// and the SaveChanges interceptor refuses to persist one whose TenantId differs from the current tenant.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>
/// Marks an entity that is either owned by a tenant (TenantId != null) or shared/system-wide (TenantId == null).
/// Tenants can READ shared rows but only system administrators can create/modify them.
/// Used for provider configurations and templates ("Support both shared and tenant-specific ... where appropriate").
/// </summary>
public interface IOptionallyTenantOwned
{
    Guid? TenantId { get; }
}
