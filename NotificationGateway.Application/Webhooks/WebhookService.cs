using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Webhooks;

namespace NotificationGateway.Application.Webhooks;

/// <summary>Raw HTTP callback, transport-independent (built by the API controller).</summary>
public sealed record WebhookRequest(
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    IReadOnlyDictionary<string, string> Form)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public enum ReceiptStatus { Delivered, Failed, Other }

public sealed record ProviderReceipt(string EventId, string ProviderMessageId, ReceiptStatus Status, string? Reason, DateTimeOffset? OccurredAt);

/// <summary>Provider specific callback parsing + signature verification. One per provider key that sends receipts.</summary>
public interface IProviderWebhookHandler
{
    string ProviderName { get; }
    bool Verify(WebhookRequest request, string signingSecret);
    IReadOnlyList<ProviderReceipt> Parse(WebhookRequest request);
}

public sealed record WebhookResult(int Received, int Applied, int Duplicates, int Unmatched);

public sealed record WebhookRegistrationDto(Guid Id, Guid ProviderConfigurationId, string ProviderKey, string CallbackPath, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Returned once at creation/rotation; the secret is never readable again.</summary>
public sealed record WebhookSecretDto(Guid Id, string CallbackPath, string SigningSecret);

public sealed record CreateWebhookRequest(Guid ProviderConfigurationId, string? SigningSecret);

public sealed record RotateWebhookSecretRequest(string? SigningSecret, int GracePeriodMinutes = 60);

/// <summary>
/// Inbound provider callbacks. Order of checks matters:
///  1. The unguessable public token identifies the webhook → tenant + provider configuration (ownership BEFORE payload).
///  2. Signature verified with that webhook's secret (current, or previous within the rotation grace period).
///  3. Each event recorded once (replay protection).
///  4. A delivery is updated only if its ProviderMessageId belongs to that same tenant AND configuration.
///  5. State changes go through the domain (out-of-order receipts never move a terminal state backwards).
/// </summary>
public sealed class WebhookService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    ISecretProtector protector,
    IWebhookEventStore eventStore,
    IEnumerable<IProviderWebhookHandler> handlers,
    TimeProvider clock,
    ILogger<WebhookService> logger)
{
    private readonly Dictionary<string, IProviderWebhookHandler> _handlers =
        handlers.ToDictionary(h => h.ProviderName, StringComparer.OrdinalIgnoreCase);

    public async Task<WebhookResult> HandleAsync(string providerKey, string publicToken, WebhookRequest request, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(providerKey, out var handler)) throw new NotFoundException("Webhook", providerKey);

        NotificationWebhook? webhook;
        using (tenantContext.BeginSystemScope())
        {
            webhook = await db.Webhooks.AsNoTracking()
                .FirstOrDefaultAsync(w => w.PublicToken == publicToken && w.ProviderKey == providerKey && w.IsActive, ct);
        }
        if (webhook is null) throw new NotFoundException("Webhook", "token");

        using var tenantScope = tenantContext.BeginScope(webhook.TenantId);
        var now = clock.GetUtcNow();

        if (!VerifyWithAnySecret(handler, request, webhook, now))
        {
            logger.LogWarning("Rejected {Provider} callback with an invalid signature for webhook {WebhookId}", providerKey, webhook.Id);
            throw new WebhookSignatureException("Invalid webhook signature.");
        }

        var receipts = handler.Parse(request);
        int applied = 0, duplicates = 0, unmatched = 0;

        foreach (var receipt in receipts)
        {
            if (!await eventStore.TryRecordAsync(providerKey, receipt.EventId, webhook.TenantId, now, ct))
            {
                duplicates++;
                continue;
            }

            var delivery = await db.Deliveries.FirstOrDefaultAsync(d =>
                d.LastProviderConfigurationId == webhook.ProviderConfigurationId && d.ProviderMessageId == receipt.ProviderMessageId, ct);
            if (delivery is null)
            {
                unmatched++;
                continue;
            }

            var changed = receipt.Status switch
            {
                ReceiptStatus.Delivered => delivery.ConfirmDelivered(receipt.OccurredAt ?? now, now),
                ReceiptStatus.Failed => delivery.ConfirmFailedByProvider(receipt.Reason ?? "Provider reported the message as undeliverable.", now),
                _ => false
            };
            if (changed) applied++;
        }

        await db.SaveChangesAsync(ct);
        return new WebhookResult(receipts.Count, applied, duplicates, unmatched);
    }

    /// <summary>
    /// Subscription verification handshake (Meta: GET ?hub.mode=subscribe&amp;hub.verify_token=..&amp;hub.challenge=..).
    /// The verify token configured at the provider is the webhook's own public token, so no extra secret is needed.
    /// Returns the challenge to echo, or null (→ 404) when the webhook/token does not match.
    /// </summary>
    public async Task<string?> VerifySubscriptionAsync(string providerKey, string publicToken, string? mode, string? verifyToken, string? challenge,
        CancellationToken ct)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(challenge) || verifyToken != publicToken) return null;
        using var systemScope = tenantContext.BeginSystemScope();
        var exists = await db.Webhooks.AsNoTracking().AnyAsync(w => w.PublicToken == publicToken && w.ProviderKey == providerKey && w.IsActive, ct);
        return exists ? challenge : null;
    }

    private bool VerifyWithAnySecret(IProviderWebhookHandler handler, WebhookRequest request, NotificationWebhook webhook, DateTimeOffset now)
    {
        var binding = SecretBindings.Webhook(webhook.Id);
        if (handler.Verify(request, protector.Unprotect(webhook.EncryptedSigningSecret, binding))) return true;
        return webhook.PreviousEncryptedSigningSecret is not null && webhook.PreviousSecretValidUntil > now
               && handler.Verify(request, protector.Unprotect(webhook.PreviousEncryptedSigningSecret, binding));
    }

    // ------------------------------------------------------------------ registration (tenant admin)

    public async Task<IReadOnlyList<WebhookRegistrationDto>> ListAsync(CancellationToken ct)
    {
        tenantContext.RequireTenantId();
        var rows = await db.Webhooks.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return rows.Select(w => new WebhookRegistrationDto(w.Id, w.ProviderConfigurationId, w.ProviderKey, Path(w), w.IsActive, w.CreatedAt)).ToList();
    }

    public async Task<WebhookSecretDto> CreateAsync(CreateWebhookRequest request, CancellationToken ct)
    {
        var tenantId = tenantContext.RequireTenantId();
        var config = await db.ProviderConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ProviderConfigurationId, ct)
                     ?? throw new NotFoundException("ProviderConfiguration", request.ProviderConfigurationId);
        if (!_handlers.ContainsKey(config.ProviderKey))
            throw RequestValidationException.For("ProviderConfigurationId", $"Provider '{config.ProviderKey}' does not send delivery receipts.");

        var secret = string.IsNullOrWhiteSpace(request.SigningSecret) ? RandomToken(32) : request.SigningSecret.Trim();
        var webhook = new NotificationWebhook(tenantId, config.Id, config.ProviderKey, RandomToken(32), "pending", clock.GetUtcNow());
        // Bind the envelope to the webhook id so it cannot be copied to another webhook row.
        webhook.RotateSecret(protector.Protect(secret, SecretBindings.Webhook(webhook.Id)), TimeSpan.Zero, clock.GetUtcNow());

        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync(ct);
        return new WebhookSecretDto(webhook.Id, Path(webhook), secret);
    }

    public async Task<WebhookSecretDto> RotateSecretAsync(Guid id, RotateWebhookSecretRequest request, CancellationToken ct)
    {
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        var secret = string.IsNullOrWhiteSpace(request.SigningSecret) ? RandomToken(32) : request.SigningSecret.Trim();
        webhook.RotateSecret(protector.Protect(secret, SecretBindings.Webhook(webhook.Id)),
            TimeSpan.FromMinutes(Math.Clamp(request.GracePeriodMinutes, 0, 7 * 24 * 60)), clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new WebhookSecretDto(webhook.Id, Path(webhook), secret);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct)
    {
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Webhook", id);
        webhook.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    private static string Path(NotificationWebhook w) => $"/api/webhooks/{w.ProviderKey}/{w.PublicToken}";

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
