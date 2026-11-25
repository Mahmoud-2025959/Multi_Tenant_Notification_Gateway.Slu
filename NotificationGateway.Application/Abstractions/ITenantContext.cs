namespace NotificationGateway.Application.Abstractions;

/// <summary>
/// The tenant the current unit of work runs for.
///
/// HTTP requests: resolved ONLY from the authenticated principal's "tenant_id" claim. A tenantId in the route/body is
/// compared with it and rejected on mismatch (never trusted on its own).
/// Background workers: set explicitly per delivery via <see cref="BeginScope"/> after the delivery row was leased.
///
/// Every EF query on a tenant-owned entity is filtered by <see cref="TenantId"/>. With no tenant and no system scope,
/// queries return nothing (secure default).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? UserId { get; }

    /// <summary>Platform operator. May manage shared configurations and read across tenants in admin endpoints.</summary>
    bool IsSystemAdmin { get; }

    /// <summary>
    /// True only inside infrastructure code that must see all tenants (dispatcher leasing, outbox relay, webhooks
    /// before the owning tenant is known). Never set from an HTTP request of a tenant user.
    /// </summary>
    bool IsSystemScope { get; }

    Guid RequireTenantId();

    /// <summary>Run the following work as <paramref name="tenantId"/>. Dispose to restore the previous scope.</summary>
    IDisposable BeginScope(Guid tenantId, string? userId = null);

    /// <summary>Run cross-tenant infrastructure work. Dispose to restore.</summary>
    IDisposable BeginSystemScope();
}
