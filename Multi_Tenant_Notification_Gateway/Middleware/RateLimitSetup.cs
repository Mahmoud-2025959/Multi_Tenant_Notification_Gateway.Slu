using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Multi_Tenant_Notification_Gateway.Middleware;

/// <summary>
/// Per-tenant rate limit on the send API (Tenant.RequestsPerMinute). Separate from quotas: rate limits protect the
/// gateway from bursts, quotas cap how many deliveries a tenant may consume per day/month.
/// One noisy tenant cannot starve the others because every tenant has its own partition.
/// Note: limits are per API instance; with N instances the effective limit is N × RequestsPerMinute (documented).
/// </summary>
public static class RateLimitSetup
{
    public const string TenantSendPolicy = "tenant-send";

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                await ctx.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = 429,
                    Title = "Rate limit exceeded",
                    Detail = "Too many requests for this tenant. Retry later."
                }, cancellationToken: ct);
            };

            options.AddPolicy(TenantSendPolicy, httpContext =>
            {
                var tenant = httpContext.Items[TenantResolutionMiddleware.TenantItemKey] as TenantSnapshot;
                var key = tenant is null ? "anonymous" : $"{tenant.Id:N}:{tenant.RequestsPerMinute}";
                var limit = tenant?.RequestsPerMinute ?? 60;

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });
        return services;
    }
}
