using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Tenants;

namespace NotificationGateway.Application.Dispatching;

public static class RetryPolicy
{
    /// <summary>delay = min(base * 2^(round-1), max) ± 20% jitter (jitter avoids retry storms after a vendor outage).</summary>
    public static TimeSpan Backoff(int round, int baseSeconds, int maxSeconds, Random? random = null)
    {
        var exponent = Math.Clamp(round - 1, 0, 20);
        var seconds = Math.Min(baseSeconds * Math.Pow(2, exponent), maxSeconds);
        var jitter = 1 + ((random ?? Random.Shared).NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(Math.Max(1, seconds * jitter));
    }
}

public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public int OpenSeconds { get; set; } = 60;
}

public enum ProcessResult { Skipped, Delivered, Sent, Retrying, Failed, DeadLettered, Uncertain, Suppressed, Deferred }

/// <summary>
/// Processes ONE leased delivery: tenant policy → user preferences → provider chain with fallback → classification.
///
/// Fallback rules (docs/Notification_Routing_and_Fallback.md):
///  * TransientFailure / ProviderFailure → immediately try the next provider in the chain (same round).
///  * RecipientRejected → Failed. Another provider would reject the same address; no fallback.
///  * Uncertain (timeout after the request left us) → NO fallback unless the provider de-duplicates by idempotency key,
///    because a second provider could deliver a duplicate SMS. Wait for a receipt/reconciliation instead.
///  * Whole chain failed → retry later with exponential backoff, until MaxAttempts → DeadLettered.
///  * Open circuit → provider skipped without consuming an attempt.
/// </summary>
public sealed class DeliveryProcessor(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    IProviderRegistry registry,
    ProviderResolver providerResolver,
    ISecretProtector protector,
    CircuitBreakerOptions circuit,
    TimeProvider clock,
    ILogger<DeliveryProcessor> logger)
{
    public async Task<ProcessResult> ProcessAsync(Guid deliveryId, string workerId, CancellationToken ct)
    {
        NotificationDelivery? delivery;
        using (tenantContext.BeginSystemScope())
        {
            // The worker does not know the tenant until the row is read.
            delivery = await db.Deliveries
                .Include(d => d.Notification)
                .Include(d => d.Recipient)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        }

        if (delivery is null || delivery.Status != DeliveryStatus.Processing || delivery.LockedBy != workerId)
            return ProcessResult.Skipped;

        // From here on every query and write is constrained to the delivery's own tenant.
        using var tenantScope = tenantContext.BeginScope(delivery.TenantId);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = delivery.TenantId, ["DeliveryId"] = delivery.Id });

        var now = clock.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == delivery.TenantId, ct);
        var setting = await db.ChannelSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Channel == delivery.Channel, ct);

        // ---- tenant policy always wins
        if (!tenant.IsActive || setting is not { IsEnabled: true })
        {
            delivery.Suppress(!tenant.IsActive ? "Tenant is inactive." : $"Channel {delivery.Channel} is disabled for the tenant.", now);
            await db.SaveChangesAsync(ct);
            return ProcessResult.Suppressed;
        }

        // ---- user preferences (opt-outs, quiet hours), with critical override
        var preferenceDecision = await ApplyPreferencesAsync(delivery, tenant, now, ct);
        if (preferenceDecision is not null)
        {
            await db.SaveChangesAsync(ct);
            return preferenceDecision.Value;
        }

        // ---- provider chain (primary + fallbacks), resolved by ProviderResolver
        var usable = await providerResolver.ResolveChainAsync(delivery.Channel, ct);

        if (usable.Count == 0)
        {
            delivery.DeadLetter($"No active provider is configured for {delivery.Channel}.", now);
            await db.SaveChangesAsync(ct);
            return ProcessResult.DeadLettered;
        }

        var attemptNumber = await db.DeliveryAttempts.CountAsync(a => a.DeliveryId == delivery.Id, ct);
        var startIndex = delivery.ProviderIndex < usable.Count ? delivery.ProviderIndex : 0;
        var calledAny = false;
        string lastError = "All providers were skipped (circuit open).";

        for (var index = startIndex; index < usable.Count; index++)
        {
            if (delivery.AttemptsExhausted)
            {
                delivery.DeadLetter($"Max attempts ({delivery.MaxAttempts}) reached. Last error: {lastError}", clock.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return ProcessResult.DeadLettered;
            }

            var config = usable[index].Configuration;
            var adapter = usable[index].Adapter;
            var health = await db.ProviderHealth.FirstOrDefaultAsync(h => h.ProviderConfigurationId == config.Id, ct);
            if (health is null)
            {
                health = new ProviderHealth(config.Id);
                db.ProviderHealth.Add(health);
            }

            now = clock.GetUtcNow();
            if (!health.AllowsCall(now))
            {
                lastError = $"Provider {config.ProviderKey} skipped: circuit open until {health.OpenUntil:u}.";
                db.DeliveryAttempts.Add(SkippedAttempt(delivery, ++attemptNumber, config, now, lastError));
                continue;
            }
            if (health.State == ProviderCircuitState.Open) health.MoveToHalfOpen();

            // ---- write-ahead: commit "call in flight" BEFORE calling the provider (crash recovery, T12)
            delivery.MarkCallInFlight(config.Id, config.ProviderKey, now);
            var attempt = new DeliveryAttempt(delivery.TenantId, delivery.Id, ++attemptNumber, config.Id, config.ProviderKey, now);
            db.DeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            calledAny = true;

            var result = await CallProviderAsync(adapter, delivery, config, setting.ProviderTimeoutSeconds, ct);
            var completedAt = clock.GetUtcNow();
            var latency = (long)(completedAt - now).TotalMilliseconds;
            attempt.Complete(result.Outcome, result.ProviderMessageId, result.ErrorCode, result.ErrorMessage, result.HttpStatus, completedAt);

            if (result.Outcome is AttemptOutcome.Accepted or AttemptOutcome.RecipientRejected)
                health.RecordSuccess(latency, completedAt);
            else
                health.RecordFailure(latency, result.ErrorMessage ?? result.ErrorCode ?? result.Outcome.ToString(), completedAt,
                    circuit.FailureThreshold, TimeSpan.FromSeconds(circuit.OpenSeconds));

            switch (result.Outcome)
            {
                case AttemptOutcome.Accepted:
                    var synchronous = adapter.Descriptor.Supports(ProviderCapabilities.SynchronousDelivery);
                    delivery.RecordAccepted(result.ProviderMessageId, synchronous, completedAt);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Delivery accepted by {Provider}", config.ProviderKey);
                    return synchronous ? ProcessResult.Delivered : ProcessResult.Sent;

                case AttemptOutcome.RecipientRejected:
                    delivery.Fail($"{config.ProviderKey}: {result.ErrorCode} {result.ErrorMessage}".Trim(), completedAt);
                    await db.SaveChangesAsync(ct);
                    return ProcessResult.Failed;

                case AttemptOutcome.Uncertain when adapter.Descriptor.Supports(ProviderCapabilities.IdempotencyKey):
                    // Safe to retry the SAME provider: it will de-duplicate on our idempotency key.
                    delivery.ScheduleRetry(index, chainExhausted: false,
                        completedAt.Add(RetryPolicy.Backoff(delivery.RetryRound + 1, setting.BaseRetryDelaySeconds, setting.MaxRetryDelaySeconds)),
                        $"{config.ProviderKey}: uncertain result, will retry with the same idempotency key.", completedAt);
                    await db.SaveChangesAsync(ct);
                    return ProcessResult.Retrying;

                case AttemptOutcome.Uncertain:
                    delivery.MarkUncertain($"{config.ProviderKey}: {result.ErrorMessage ?? "outcome unknown"}. Not failing over to avoid duplicates.", completedAt);
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning("Delivery outcome uncertain on {Provider}", config.ProviderKey);
                    return ProcessResult.Uncertain;

                default:   // TransientFailure, ProviderFailure → fall back to the next provider now
                    lastError = $"{config.ProviderKey}: {result.ErrorCode} {result.ErrorMessage}".Trim();
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning("Provider {Provider} failed ({Outcome}); trying next provider", config.ProviderKey, result.Outcome);
                    break;
            }
        }

        // ---- every provider in the chain failed (or was skipped) in this round
        now = clock.GetUtcNow();
        if (delivery.AttemptsExhausted)
        {
            delivery.DeadLetter($"Max attempts ({delivery.MaxAttempts}) reached. Last error: {lastError}", now);
            await db.SaveChangesAsync(ct);
            return ProcessResult.DeadLettered;
        }

        var nextAt = now.Add(RetryPolicy.Backoff(delivery.RetryRound + 1, setting.BaseRetryDelaySeconds, setting.MaxRetryDelaySeconds));
        delivery.ScheduleRetry(0, chainExhausted: calledAny, nextAt, lastError, now);
        await db.SaveChangesAsync(ct);
        return ProcessResult.Retrying;
    }

    /// <summary>Recovers a delivery whose worker died while holding the lease.</summary>
    public async Task RecoverExpiredLeaseAsync(Guid deliveryId, CancellationToken ct)
    {
        NotificationDelivery? delivery;
        using (tenantContext.BeginSystemScope())
            delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);

        var now = clock.GetUtcNow();
        if (delivery is null || delivery.Status != DeliveryStatus.Processing || delivery.LockedUntil is null || delivery.LockedUntil > now) return;

        using var tenantScope = tenantContext.BeginScope(delivery.TenantId);
        var supportsIdempotency = false;
        if (delivery.InFlightProviderConfigurationId is { } configId)
        {
            var key = await db.ProviderConfigurations.AsNoTracking().Where(c => c.Id == configId).Select(c => c.ProviderKey).FirstOrDefaultAsync(ct);
            supportsIdempotency = key is not null && registry.TryGet(key, out var adapter) && adapter.Descriptor.Supports(ProviderCapabilities.IdempotencyKey);

            // Close the dangling attempt row so history shows what happened.
            var dangling = await db.DeliveryAttempts.Where(a => a.DeliveryId == delivery.Id && a.Outcome == null).ToListAsync(ct);
            foreach (var a in dangling)
                a.Complete(AttemptOutcome.Uncertain, null, "worker_crashed", "Worker stopped during the provider call.", null, now);
        }

        delivery.RecoverExpiredLease(supportsIdempotency, now);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Recovered expired lease for delivery {DeliveryId}; new status {Status}", delivery.Id, delivery.Status);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ProcessResult?> ApplyPreferencesAsync(NotificationDelivery delivery, Tenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        var userId = delivery.Recipient?.UserId;
        if (userId is null) return null;

        var pref = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (pref is null) return null;

        var criticalOverride = delivery.Priority == NotificationPriority.Critical && tenant.CriticalOverridesUserPreferences && pref.AllowCriticalOverride;

        if (!criticalOverride &&
            (pref.DisabledChannelSet.Contains(delivery.Channel) || pref.DisabledTypeSet.Contains(delivery.Notification.NotificationType)))
        {
            delivery.Suppress("User opted out of this channel or notification type.", now);
            return ProcessResult.Suppressed;
        }

        // Preferred channels: skip a non-preferred channel when the same notification also reaches this user on a
        // preferred one. InApp is always kept (it is the user's record of the notification).
        var preferred = pref.PreferredChannelSet;
        if (!criticalOverride && preferred.Count > 0 && delivery.Channel != NotificationChannel.InApp && !preferred.Contains(delivery.Channel))
        {
            var preferredList = preferred.ToList();
            var reachedOnPreferred = await db.Deliveries.AnyAsync(d =>
                d.NotificationId == delivery.NotificationId && d.RecipientId == delivery.RecipientId && preferredList.Contains(d.Channel), ct);
            if (reachedOnPreferred)
            {
                delivery.Suppress($"User prefers {string.Join(", ", preferredList)} for this notification.", now);
                return ProcessResult.Suppressed;
            }
        }

        // Quiet hours do not apply to the in-app inbox (it never interrupts the user).
        if (!criticalOverride && delivery.Channel != NotificationChannel.InApp)
        {
            var resumeAt = QuietHoursEnd(pref, pref.TimeZoneId ?? tenant.TimeZoneId, now);
            if (resumeAt is not null)
            {
                delivery.Defer(resumeAt.Value, $"Deferred by the user's quiet hours until {resumeAt:u}.", now);
                return ProcessResult.Deferred;
            }
        }
        return null;
    }

    internal static DateTimeOffset? QuietHoursEnd(NotificationUserPreference pref, string timeZoneId, DateTimeOffset nowUtc)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz)) tz = TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var end = pref.QuietWindowEndIfInside(TimeOnly.FromDateTime(local.DateTime));
        if (end is null) return null;

        var endLocal = local.Date.Add(end.Value.ToTimeSpan());
        if (endLocal <= local.DateTime) endLocal = endLocal.AddDays(1);
        return new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal)).ToUniversalTime();
    }

    private async Task<NotificationResult> CallProviderAsync(INotificationProvider adapter, NotificationDelivery delivery,
        TenantProviderConfiguration config, int timeoutSeconds, CancellationToken ct)
    {
        ProviderConfigurationContext context;
        try
        {
            var secretsJson = protector.Unprotect(config.EncryptedSecrets, SecretBindings.ProviderConfig(config.Id));
            context = new ProviderConfigurationContext(config.Id, config.TenantId,
                JsonDocument.Parse(string.IsNullOrWhiteSpace(config.SettingsJson) ? "{}" : config.SettingsJson).RootElement,
                JsonSerializer.Deserialize<Dictionary<string, string>>(secretsJson) ?? new(),
                config.SenderIdentity);
        }
        catch (Exception ex)
        {
            // Nothing was sent: safe to fail over.
            logger.LogError(ex, "Could not decrypt configuration {ConfigurationId}", config.Id);
            return NotificationResult.ProviderFailure("secret_unreadable", "Provider secrets could not be decrypted.");
        }

        var message = new NotificationMessage(delivery.TenantId, delivery.NotificationId, delivery.Id, delivery.ProviderIdempotencyKey,
            delivery.Channel, delivery.Notification.NotificationType, delivery.Priority, delivery.Recipient?.UserId, delivery.Address,
            delivery.Subject, delivery.Body ?? string.Empty, context);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var sw = Stopwatch.StartNew();
        try
        {
            return await adapter.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired: the request may already be at the provider.
            return NotificationResult.Uncertain("timeout", $"No response from {adapter.ProviderName} within {timeoutSeconds}s ({sw.ElapsedMilliseconds} ms).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Adapter {Provider} threw unexpectedly", adapter.ProviderName);
            return NotificationResult.Uncertain("adapter_exception", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static DeliveryAttempt SkippedAttempt(NotificationDelivery delivery, int number, TenantProviderConfiguration config, DateTimeOffset now, string reason)
    {
        var attempt = new DeliveryAttempt(delivery.TenantId, delivery.Id, number, config.Id, config.ProviderKey, now);
        attempt.Complete(AttemptOutcome.Skipped, null, "circuit_open", reason, null, now);
        return attempt;
    }
}
