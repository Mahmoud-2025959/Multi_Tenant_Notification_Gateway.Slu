using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Multi_Tenant_Notification_Gateway.Auth;
using Multi_Tenant_Notification_Gateway.Middleware;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Settings;
using NotificationGateway.Application.Inbox;
using NotificationGateway.Application.Notifications;
using NotificationGateway.Application.Webhooks;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>
/// Central Notification API (spec §5.A). Business modules send here without knowing any provider.
/// </summary>
[ApiController]
public sealed class NotificationsController(
    NotificationOrchestrator orchestrator,
    NotificationQueryService queries,
    TenantDirectory tenants,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>
    /// Validates tenant + request, resolves routing and templates, queues one delivery per (recipient, channel) and
    /// returns 202 with a tracking id. "tenantId" in the body is optional and must match the authenticated tenant.
    /// Idempotency-Key header (or body field): the same key returns the original result instead of sending again.
    /// </summary>
    [HttpPost("api/notifications/send")]
    [HttpPost("api/notifications")]
    [Authorize(Policy = Policies.Sender)]
    [EnableRateLimiting(RateLimitSetup.TenantSendPolicy)]
    public async Task<ActionResult<SendNotificationResponse>> Send(
        SendNotificationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        await tenants.EnsureIsCurrentAsync(request.TenantId, tenantContext, ct);
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && !string.IsNullOrWhiteSpace(request.IdempotencyKey) && idempotencyKey != request.IdempotencyKey)
            throw RequestValidationException.For("IdempotencyKey", "Header and body idempotency keys differ.");

        var response = await orchestrator.SendAsync(request with { IdempotencyKey = request.IdempotencyKey ?? idempotencyKey }, ct);
        if (response.Replayed) Response.Headers["Idempotent-Replayed"] = "true";
        return AcceptedAtAction(nameof(Get), new { id = response.NotificationId }, response);
    }

    /// <summary>Tracking: every delivery with its status and every provider attempt.</summary>
    [HttpGet("api/notifications/{id:guid}")]
    [Authorize(Policy = Policies.Sender)]
    public Task<NotificationDetailDto> Get(Guid id, CancellationToken ct) => queries.GetAsync(id, ct);

    /// <summary>Notification history (filters: type, status, channel, provider, priority, date range).</summary>
    [HttpGet("api/notifications")]
    [HttpGet("api/tenants/{tenantId}/notifications")]
    [Authorize(Policy = Policies.Sender)]
    public Task<PagedResult<NotificationListItemDto>> History([FromQuery] NotificationQuery query, CancellationToken ct) => queries.ListAsync(query, ct);

    /// <summary>Cancels deliveries that were not handed to a provider yet.</summary>
    [HttpPost("api/notifications/{id:guid}/cancel")]
    [Authorize(Policy = Policies.Sender)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Ok(new { cancelled = await orchestrator.CancelAsync(id, ct) });

    /// <summary>Dead-lettered, failed and uncertain deliveries of the tenant.</summary>
    [HttpGet("api/tenants/{tenantId}/dead-letters")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public Task<PagedResult<DeliveryDetailDto>> DeadLetters([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        queries.ListProblemDeliveriesAsync(page, pageSize, ct);

    /// <summary>Replays a dead-lettered/failed/uncertain delivery (fresh attempt budget).</summary>
    [HttpPost("api/tenants/{tenantId}/dead-letters/{deliveryId:guid}/requeue")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> Requeue(Guid deliveryId, CancellationToken ct)
    {
        await orchestrator.RequeueAsync(deliveryId, ct);
        return Accepted();
    }
}

/// <summary>The calling user's own preferences (spec §5.G).</summary>
[ApiController]
[Route("api/me/preferences")]
[Authorize(Policy = Policies.TenantMember)]
public sealed class PreferencesController(UserPreferenceService service) : ControllerBase
{
    [HttpGet]
    public Task<UserPreferenceDto> Get(CancellationToken ct) => service.GetMineAsync(ct);

    [HttpPut]
    public Task<UserPreferenceDto> Update(UpdateUserPreferenceRequest request, CancellationToken ct) => service.UpdateMineAsync(request, ct);
}

/// <summary>The calling user's in-app inbox.</summary>
[ApiController]
[Route("api/me/inbox")]
[Authorize(Policy = Policies.TenantMember)]
public sealed class InboxController(InboxService service) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<InboxItemDto>> List([FromQuery] bool unreadOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default) => service.ListAsync(unreadOnly, page, pageSize, ct);

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) => Ok(new { count = await service.UnreadCountAsync(ct) });

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct) { await service.MarkReadAsync(id, ct); return NoContent(); }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct) => Ok(new { marked = await service.MarkAllReadAsync(ct) });
}

/// <summary>
/// Public provider callbacks. No JWT: ownership comes from the unguessable token in the URL and the signature is
/// verified with that webhook's secret before anything is applied.
/// </summary>
[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
public sealed class ProviderWebhooksController(WebhookService service) : ControllerBase
{
    /// <summary>Provider subscription handshake (e.g. Meta WhatsApp "hub.challenge").</summary>
    [HttpGet("{providerKey}/{token}")]
    public async Task<IActionResult> Verify(string providerKey, string token,
        [FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge, CancellationToken ct)
    {
        var echo = await service.VerifySubscriptionAsync(providerKey, token, mode, verifyToken, challenge, ct);
        return echo is null ? NotFound() : Content(echo, "text/plain");
    }

    [HttpPost("{providerKey}/{token}")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Receive(string providerKey, string token, CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var form = new Dictionary<string, string>();
        if (Request.HasFormContentType)
        {
            var parsed = await Request.ReadFormAsync(ct);
            foreach (var kv in parsed) form[kv.Key] = kv.Value.ToString();
        }

        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}{Request.QueryString}";

        var result = await service.HandleAsync(providerKey, token, new WebhookRequest(url, headers, body, form), ct);
        return Ok(result);
    }
}
