using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Tenancy;

namespace Multi_Tenant_Notification_Gateway.Middleware;

public sealed record TenantSnapshot(Guid Id, string Code, bool IsActive, int RequestsPerMinute);

/// <summary>
/// Tenant identification and resolution (spec §3). Runs after authentication and routing.
///
///  * The tenant comes from the validated token's "tenant_id" claim.
///  * A route value {tenantId} (e.g. /api/tenants/tenant-001/providers) is NEVER trusted on its own: it is resolved
///    (code or GUID) and must equal the token's tenant, otherwise 403. Unknown tenants also get 403, so the API does
///    not reveal which tenant codes exist.
///  * Only a SystemAdmin may act for another tenant, via the route value or the X-Tenant-Id header. AuditLogs record
///    the admin's user id for every change made that way.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string AdminTenantHeader = "X-Tenant-Id";
    public const string TenantItemKey = "ng.tenant";
    public const string TenantRouteKey = "tenantId";

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext, NotificationGatewayDbContext db, IMemoryCache cache)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var isAdmin = user.IsInRole(Roles.SystemAdmin);
        var userId = user.FindFirst(GatewayClaims.Subject)?.Value;
        var claim = user.FindFirst(GatewayClaims.TenantId)?.Value;

        TenantSnapshot? tenant = null;
        if (!string.IsNullOrEmpty(claim))
        {
            tenant = await LookupAsync(claim, db, cache);
            if (tenant is null)
            {
                await Deny(context, "Tenant is unknown or inactive.");
                return;
            }
        }

        // Admin acting for a tenant (header).
        if (isAdmin && context.Request.Headers.TryGetValue(AdminTenantHeader, out var header) && !string.IsNullOrWhiteSpace(header))
        {
            tenant = await LookupAsync(header.ToString(), db, cache);
            if (tenant is null)
            {
                await Deny(context, "Tenant in X-Tenant-Id was not found.");
                return;
            }
        }

        // Route {tenantId}: must match (or, for admins, selects the tenant).
        if (context.GetRouteValue(TenantRouteKey) is string routeTenant && !string.IsNullOrWhiteSpace(routeTenant))
        {
            var routed = await LookupAsync(routeTenant, db, cache);
            if (routed is null || (!isAdmin && routed.Id != tenant?.Id))
            {
                await Deny(context, "You can only access your own tenant.");
                return;
            }
            tenant = routed;
        }

        if (tenant is null && !isAdmin)
        {
            await Deny(context, "The token is not associated with a tenant.");
            return;
        }
        if (tenant is { IsActive: false } && !isAdmin)
        {
            await Deny(context, "Tenant is inactive.");
            return;
        }

        if (tenant is not null) context.Items[TenantItemKey] = tenant;
        tenantContext.SetFromAuthenticatedUser(tenant?.Id, userId, isAdmin);
        await next(context);
    }

    private static Task<TenantSnapshot?> LookupAsync(string reference, NotificationGatewayDbContext db, IMemoryCache cache) =>
        cache.GetOrCreateAsync($"tenant-ref:{reference.Trim().ToLowerInvariant()}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var query = db.Tenants.AsNoTracking();
            query = Guid.TryParse(reference, out var id)
                ? query.Where(t => t.Id == id)
                : query.Where(t => t.Code == reference.Trim().ToLower());
            return await query.Select(t => new TenantSnapshot(t.Id, t.Code, t.IsActive, t.RequestsPerMinute)).FirstOrDefaultAsync();
        });

    private static Task Deny(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Tenant access denied", Detail = detail },
            options: null, contentType: "application/problem+json");
    }
}
