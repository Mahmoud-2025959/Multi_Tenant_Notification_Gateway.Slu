using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Reports;
using NotificationGateway.Domain.Enums;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>Query-string filters shared by every report (spec §5.H).</summary>
public sealed class ReportQuery
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public NotificationChannel? Channel { get; set; }
    public string? Provider { get; set; }
    public string? NotificationType { get; set; }
    public DeliveryStatus? Status { get; set; }

    public ReportFilter ToFilter(TimeProvider clock)
    {
        var end = To ?? clock.GetUtcNow();
        var start = From ?? end.AddDays(-30);
        if (start >= end) throw RequestValidationException.For("from", "'from' must be before 'to'.");
        if (end - start > TimeSpan.FromDays(366)) throw RequestValidationException.For("from", "The range is limited to one year.");
        return new ReportFilter(start, end, Channel, Provider, NotificationType, Status);
    }
}

[ApiController]
[Route("api/tenants/{tenantId}/reports")]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class NotificationReportsController(ReportService service, TimeProvider clock) : ControllerBase
{
    /// <summary>Totals, success/failure rates, status by channel, retries, dead letters, quota consumption.</summary>
    [HttpGet("summary")]
    public Task<DeliverySummaryReport> Summary([FromQuery] ReportQuery query, CancellationToken ct) =>
        service.DeliverySummaryAsync(query.ToFilter(clock), ct);

    /// <summary>Per provider: calls, accepted, failures, uncertain, skipped (circuit open), latency.</summary>
    [HttpGet("providers")]
    public Task<IReadOnlyList<ProviderPerformanceRow>> Providers([FromQuery] ReportQuery query, CancellationToken ct) =>
        service.ProviderPerformanceAsync(query.ToFilter(clock), ct);

    [HttpGet("cost")]
    public Task<IReadOnlyList<CostRow>> Cost([FromQuery] ReportQuery query, CancellationToken ct) =>
        service.CostEstimateAsync(query.ToFilter(clock), ct);

    [HttpGet("daily-volume")]
    public Task<IReadOnlyList<DailyVolumeRow>> Daily([FromQuery] ReportQuery query, CancellationToken ct) =>
        service.DailyVolumeAsync(query.ToFilter(clock), ct);
}

/// <summary>Platform-wide usage per tenant (filter by tenant = pick the row, or use the tenant reports above).</summary>
[ApiController]
[Route("api/admin/reports")]
[Authorize(Policy = Policies.SystemAdmin)]
public sealed class AdminReportsController(AdminReportService service, TimeProvider clock) : ControllerBase
{
    [HttpGet("tenant-usage")]
    public Task<IReadOnlyList<TenantUsageRow>> TenantUsage([FromQuery] ReportQuery query, CancellationToken ct)
    {
        var f = query.ToFilter(clock);
        return service.TenantUsageAsync(f.From, f.To, ct);
    }
}
