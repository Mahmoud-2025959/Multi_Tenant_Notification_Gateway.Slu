using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Settings;
using NotificationGateway.Application.Webhooks;
using NotificationGateway.Domain.Enums;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>
/// Tenant notification settings (spec §5.C). {tenantId} accepts the tenant code ("tenant-001") or its GUID and is
/// validated against the token by TenantResolutionMiddleware: a tenant admin can only reach their own tenant.
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId}")]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class TenantNotificationSettingsController(
    TenantNotificationSettingsService settings,
    ChannelSettingsService channels,
    ProviderConfigurationService providers,
    RoutingRuleService routing,
    QuotaSettingsService quotas,
    WebhookService webhooks) : ControllerBase
{
    // ------------------------------------------------------------ overall settings

    /// <summary>Policies, every channel with its provider chain, and quotas with current consumption.</summary>
    [HttpGet("notification-settings")]
    public Task<NotificationSettingsDto> GetSettings(CancellationToken ct) => settings.GetAsync(ct);

    [HttpPut("notification-settings")]
    public Task<NotificationSettingsDto> UpdateSettings(UpdateNotificationSettingsRequest request, CancellationToken ct) => settings.UpdateAsync(request, ct);

    // ------------------------------------------------------------ channels (activation, retry policy, provider chain)

    [HttpGet("channels")]
    public Task<IReadOnlyList<ChannelSettingDto>> Channels(CancellationToken ct) => channels.ListAsync(ct);

    [HttpPut("channels/{channelId}")]
    public Task<ChannelSettingDto> UpdateChannel(NotificationChannel channelId, UpdateChannelSettingRequest request, CancellationToken ct) =>
        channels.UpsertAsync(channelId, request, ct);

    /// <summary>Ordered chain: [0] = primary provider, [1..] = fallbacks.</summary>
    [HttpGet("channels/{channelId}/providers")]
    public Task<IReadOnlyList<ProviderChainItemDto>> GetChain(NotificationChannel channelId, CancellationToken ct) => providers.GetChainAsync(channelId, ct);

    [HttpPut("channels/{channelId}/providers")]
    public Task<IReadOnlyList<ProviderChainItemDto>> SetChain(NotificationChannel channelId, SetProviderChainRequest request, CancellationToken ct) =>
        providers.SetChainAsync(channelId, request, ct);

    // ------------------------------------------------------------ provider configurations (secrets are write-only)

    [HttpGet("providers")]
    public Task<IReadOnlyList<ProviderConfigurationDto>> Providers([FromQuery] NotificationChannel? channel, CancellationToken ct) =>
        providers.ListAsync(channel, ct);

    [HttpGet("providers/{providerId:guid}")]
    public Task<ProviderConfigurationDto> GetProvider(Guid providerId, CancellationToken ct) => providers.GetAsync(providerId, ct);

    [HttpPost("providers")]
    public async Task<ActionResult<ProviderConfigurationDto>> CreateProvider(string tenantId, CreateProviderConfigurationRequest request, CancellationToken ct)
    {
        var created = await providers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetProvider), new { tenantId, providerId = created.Id }, created);
    }

    [HttpPut("providers/{providerId:guid}")]
    public Task<ProviderConfigurationDto> UpdateProvider(Guid providerId, UpdateProviderConfigurationRequest request, CancellationToken ct) =>
        providers.UpdateAsync(providerId, request, ct);

    /// <summary>Secret rotation: new envelope, SecretVersion + 1. Values are never returned.</summary>
    [HttpPut("providers/{providerId:guid}/secrets")]
    public Task<ProviderConfigurationDto> RotateSecrets(Guid providerId, RotateSecretsRequest request, CancellationToken ct) =>
        providers.RotateSecretsAsync(providerId, request, ct);

    [HttpDelete("providers/{providerId:guid}")]
    public async Task<IActionResult> DeleteProvider(Guid providerId, CancellationToken ct)
    {
        await providers.DeactivateAsync(providerId, ct);
        return NoContent();
    }

    // ------------------------------------------------------------ routing rules

    [HttpGet("routing-rules")]
    public Task<IReadOnlyList<RoutingRuleDto>> RoutingRules(CancellationToken ct) => routing.ListAsync(ct);

    [HttpPost("routing-rules")]
    public Task<RoutingRuleDto> CreateRule(UpsertRoutingRuleRequest request, CancellationToken ct) => routing.CreateAsync(request, ct);

    [HttpPut("routing-rules/{ruleId:guid}")]
    public Task<RoutingRuleDto> UpdateRule(Guid ruleId, UpsertRoutingRuleRequest request, CancellationToken ct) => routing.UpdateAsync(ruleId, request, ct);

    [HttpDelete("routing-rules/{ruleId:guid}")]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken ct) { await routing.DeleteAsync(ruleId, ct); return NoContent(); }

    // ------------------------------------------------------------ quotas (read: tenant admin, write: platform)

    [HttpGet("quotas")]
    public Task<IReadOnlyList<QuotaDto>> Quotas(CancellationToken ct) => quotas.ListAsync(ct);

    [HttpPut("quotas")]
    [Authorize(Policy = Policies.SystemAdmin)]
    public Task<QuotaDto> UpsertQuota(UpsertQuotaRequest request, CancellationToken ct) => quotas.UpsertAsync(request, ct);

    [HttpDelete("quotas/{quotaId:guid}")]
    [Authorize(Policy = Policies.SystemAdmin)]
    public async Task<IActionResult> DeleteQuota(Guid quotaId, CancellationToken ct) { await quotas.DeleteAsync(quotaId, ct); return NoContent(); }

    // ------------------------------------------------------------ provider callback (webhook) registrations

    [HttpGet("webhooks")]
    public Task<IReadOnlyList<WebhookRegistrationDto>> Webhooks(CancellationToken ct) => webhooks.ListAsync(ct);

    /// <summary>The signing secret is returned ONCE in this response.</summary>
    [HttpPost("webhooks")]
    public Task<WebhookSecretDto> CreateWebhook(CreateWebhookRequest request, CancellationToken ct) => webhooks.CreateAsync(request, ct);

    [HttpPost("webhooks/{webhookId:guid}/rotate-secret")]
    public Task<WebhookSecretDto> RotateWebhookSecret(Guid webhookId, RotateWebhookSecretRequest request, CancellationToken ct) =>
        webhooks.RotateSecretAsync(webhookId, request, ct);

    [HttpDelete("webhooks/{webhookId:guid}")]
    public async Task<IActionResult> DeleteWebhook(Guid webhookId, CancellationToken ct) { await webhooks.DeactivateAsync(webhookId, ct); return NoContent(); }
}
