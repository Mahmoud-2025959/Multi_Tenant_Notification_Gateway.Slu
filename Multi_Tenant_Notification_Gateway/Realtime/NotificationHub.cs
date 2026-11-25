using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Abstractions;

namespace Multi_Tenant_Notification_Gateway.Realtime;

/// <summary>
/// Clients connect to /hubs/notifications with their JWT. Each connection joins exactly one group derived from the
/// token (tenant + user), so the server can never push a message to another tenant's user by mistake.
/// Client method: "notification" (RealtimeMessage).
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public static string GroupFor(Guid tenantId, string userId) => $"t:{tenantId:N}:u:{userId}";

    public override async Task OnConnectedAsync()
    {
        var tenant = Context.User?.FindFirst(GatewayClaims.TenantId)?.Value;
        var user = Context.User?.FindFirst(GatewayClaims.Subject)?.Value;
        if (!Guid.TryParse(tenant, out var tenantId) || string.IsNullOrEmpty(user))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenantId, user));
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRRealtimePublisher(IHubContext<NotificationHub> hub) : IRealtimePublisher
{
    public Task PublishToUserAsync(Guid tenantId, string userId, RealtimeMessage message, CancellationToken ct) =>
        hub.Clients.Group(NotificationHub.GroupFor(tenantId, userId)).SendAsync("notification", message, ct);
}
