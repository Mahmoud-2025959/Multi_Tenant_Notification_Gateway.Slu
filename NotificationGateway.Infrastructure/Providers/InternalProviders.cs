using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Notifications;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Infrastructure.Persistence;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>
/// In-app inbox. Writing the row IS the delivery (SynchronousDelivery). Idempotent: the unique index on DeliveryId
/// means a retry after a crash finds the existing row and reports success instead of creating a duplicate.
/// </summary>
public sealed class InAppProvider(IServiceScopeFactory scopes, TimeProvider clock) : ProviderBase
{
    public const string Key = "inapp";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.InApp;
    public override ProviderDescriptor Descriptor { get; } = new("In-app inbox",
        ProviderCapabilities.SynchronousDelivery | ProviderCapabilities.IdempotencyKey, Array.Empty<string>(), Array.Empty<string>());

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.RecipientUserId))
            return NotificationResult.RecipientRejected("no_user", "In-app notifications need a recipient userId.");

        // Own scope + own DbContext: the adapter is a singleton and must not share the dispatcher's unit of work.
        using var scope = scopes.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
        using var tenantScope = tenant.BeginScope(message.TenantId);

        var existing = await db.InAppNotifications.AsNoTracking().FirstOrDefaultAsync(n => n.DeliveryId == message.DeliveryId, ct);
        if (existing is not null) return NotificationResult.Accepted(existing.Id.ToString());

        var row = new InAppNotification(message.TenantId, message.RecipientUserId, message.DeliveryId, message.Subject, message.Body,
            message.NotificationType, clock.GetUtcNow());
        db.InAppNotifications.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return NotificationResult.Accepted(row.Id.ToString());
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent retry of the same delivery: the row exists, so it is delivered.
            var winner = await db.InAppNotifications.AsNoTracking().FirstOrDefaultAsync(n => n.DeliveryId == message.DeliveryId, ct);
            return winner is not null
                ? NotificationResult.Accepted(winner.Id.ToString())
                : NotificationResult.Transient("db_error", "Could not store the in-app notification.");
        }
    }
}

/// <summary>
/// Real-time push to connected browsers/apps through SignalR (group per tenant+user, so a message can never reach
/// another tenant's user). Fire-and-forget: SignalR cannot prove the client displayed it, so the status stays Sent.
/// </summary>
public sealed class SignalRProvider(IRealtimePublisher publisher, TimeProvider clock) : ProviderBase
{
    public const string Key = "signalr";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.SignalR;
    public override ProviderDescriptor Descriptor { get; } = new("SignalR real-time", ProviderCapabilities.IdempotencyKey,
        Array.Empty<string>(), Array.Empty<string>());

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.RecipientUserId))
            return NotificationResult.RecipientRejected("no_user", "SignalR notifications need a recipient userId.");

        try
        {
            await publisher.PublishToUserAsync(message.TenantId, message.RecipientUserId, new RealtimeMessage(message.DeliveryId,
                message.NotificationId, message.NotificationType, message.Priority.ToString(), message.Subject, message.Body, clock.GetUtcNow()), ct);
            return NotificationResult.Accepted($"signalr-{message.DeliveryId:N}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return NotificationResult.Transient("signalr_error", ex.Message);
        }
    }
}
