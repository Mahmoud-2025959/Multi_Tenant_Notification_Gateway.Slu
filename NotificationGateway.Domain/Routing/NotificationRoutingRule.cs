using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Routing;

/// <summary>
/// Tenant routing rule: "notifications of type X (and/or priority >= P) go to channels [..]".
/// Rules are evaluated by ascending <see cref="Order"/>; the first match wins.
/// NotificationType == null means "any type" (use it for a tenant default rule with a high Order).
/// </summary>
public sealed class NotificationRoutingRule : Entity, ITenantOwned
{
    private NotificationRoutingRule() { }

    public NotificationRoutingRule(
        Guid tenantId,
        string name,
        string? notificationType,
        NotificationPriority? minPriority,
        IEnumerable<NotificationChannel> channels,
        int order,
        bool allowCallerOverride)
    {
        var list = channels.Distinct().ToList();
        if (list.Count == 0) throw new DomainException("routing.channels_required", "A routing rule needs at least one channel.");

        TenantId = tenantId;
        Name = name;
        NotificationType = string.IsNullOrWhiteSpace(notificationType) ? null : notificationType.Trim();
        MinPriority = minPriority;
        Channels = string.Join(',', list.Select(c => c.ToString()));
        Order = order;
        AllowCallerOverride = allowCallerOverride;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? NotificationType { get; private set; }
    public NotificationPriority? MinPriority { get; private set; }

    /// <summary>Stored as comma separated enum names, e.g. "SignalR,InApp".</summary>
    public string Channels { get; private set; } = default!;
    public int Order { get; private set; }

    /// <summary>
    /// When true, channels explicitly requested by the caller are used (still filtered by tenant activation and user preferences).
    /// When false, the rule's channels are enforced and the caller's list is ignored.
    /// </summary>
    public bool AllowCallerOverride { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyList<NotificationChannel> ChannelList =>
        Channels.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => Enum.Parse<NotificationChannel>(c))
            .ToList();

    public bool Matches(string notificationType, NotificationPriority priority)
    {
        if (!IsActive) return false;
        if (NotificationType is not null && !string.Equals(NotificationType, notificationType, StringComparison.OrdinalIgnoreCase)) return false;
        if (MinPriority is not null && priority < MinPriority) return false;
        return true;
    }

    public void Update(string name, string? notificationType, NotificationPriority? minPriority,
        IEnumerable<NotificationChannel> channels, int order, bool allowCallerOverride, bool isActive)
    {
        var list = channels.Distinct().ToList();
        if (list.Count == 0) throw new DomainException("routing.channels_required", "A routing rule needs at least one channel.");
        Name = name;
        NotificationType = string.IsNullOrWhiteSpace(notificationType) ? null : notificationType.Trim();
        MinPriority = minPriority;
        Channels = string.Join(',', list.Select(c => c.ToString()));
        Order = order;
        AllowCallerOverride = allowCallerOverride;
        IsActive = isActive;
    }
}
