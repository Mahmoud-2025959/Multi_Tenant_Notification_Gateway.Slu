using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Preferences;

/// <summary>
/// A user's own preferences inside one tenant. One row per (TenantId, UserId).
/// Tenant policy always wins over this: a channel disabled by the tenant is never used even if the user prefers it,
/// and (when the tenant allows it) Critical notifications override opt-outs and quiet hours.
/// </summary>
public sealed class NotificationUserPreference : Entity, ITenantOwned
{
    private NotificationUserPreference() { }

    public NotificationUserPreference(Guid tenantId, string userId)
    {
        TenantId = tenantId;
        UserId = userId;
    }

    public Guid TenantId { get; private set; }
    public string UserId { get; private set; } = default!;
    public TemplateLanguage? Language { get; private set; }

    /// <summary>Comma separated channels the user opted OUT of.</summary>
    public string DisabledChannels { get; private set; } = string.Empty;

    /// <summary>
    /// Comma separated channels the user PREFERS. When a notification is routed to several channels for this user and at
    /// least one of them is preferred, the non-preferred ones are skipped (InApp is always kept: it is the user's record).
    /// Empty = no preference.
    /// </summary>
    public string PreferredChannels { get; private set; } = string.Empty;

    /// <summary>Comma separated notification types the user opted OUT of.</summary>
    public string DisabledNotificationTypes { get; private set; } = string.Empty;

    /// <summary>Quiet hours in the user's time zone. Start > End means the window crosses midnight (22:00 -> 07:00).</summary>
    public TimeOnly? QuietHoursStart { get; private set; }
    public TimeOnly? QuietHoursEnd { get; private set; }
    public string? TimeZoneId { get; private set; }

    /// <summary>The user can opt out of critical overrides only if the tenant allows it; default is to accept them.</summary>
    public bool AllowCriticalOverride { get; private set; } = true;

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlySet<NotificationChannel> DisabledChannelSet =>
        DisabledChannels.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => Enum.Parse<NotificationChannel>(c)).ToHashSet();

    public IReadOnlySet<NotificationChannel> PreferredChannelSet =>
        PreferredChannels.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => Enum.Parse<NotificationChannel>(c)).ToHashSet();

    public IReadOnlySet<string> DisabledTypeSet =>
        DisabledNotificationTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Update(TemplateLanguage? language, IEnumerable<NotificationChannel> disabledChannels, IEnumerable<string> disabledTypes,
        TimeOnly? quietStart, TimeOnly? quietEnd, string? timeZoneId, bool allowCriticalOverride, DateTimeOffset now,
        IEnumerable<NotificationChannel>? preferredChannels = null)
    {
        if ((quietStart is null) != (quietEnd is null))
            throw new DomainException("preference.quiet_hours", "Quiet hours need both a start and an end.");

        Language = language;
        var disabled = disabledChannels.Distinct().ToList();
        var preferred = (preferredChannels ?? Array.Empty<NotificationChannel>()).Distinct().ToList();
        if (preferred.Intersect(disabled).Any())
            throw new DomainException("preference.conflict", "A channel cannot be both preferred and disabled.");

        DisabledChannels = string.Join(',', disabled);
        PreferredChannels = string.Join(',', preferred);
        DisabledNotificationTypes = string.Join(',', disabledTypes.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        QuietHoursStart = quietStart;
        QuietHoursEnd = quietEnd;
        TimeZoneId = timeZoneId;
        AllowCriticalOverride = allowCriticalOverride;
        UpdatedAt = now;
    }

    /// <summary>Returns the end of the current quiet window if <paramref name="localTime"/> falls inside it, else null.</summary>
    public TimeOnly? QuietWindowEndIfInside(TimeOnly localTime)
    {
        if (QuietHoursStart is not { } start || QuietHoursEnd is not { } end) return null;
        var inside = start <= end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;   // crosses midnight
        return inside ? end : null;
    }
}
