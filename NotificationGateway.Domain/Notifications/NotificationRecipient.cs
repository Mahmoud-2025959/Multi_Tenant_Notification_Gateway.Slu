using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Notifications;

public sealed class NotificationRecipient : Entity, ITenantOwned
{
    private NotificationRecipient() { }

    internal NotificationRecipient(Guid tenantId, Guid notificationId, string? userId, string? email, string? phone, string? deviceToken, TemplateLanguage? language)
    {
        TenantId = tenantId;
        NotificationId = notificationId;
        UserId = userId;
        Email = email;
        Phone = phone;
        DeviceToken = deviceToken;
        Language = language;
    }

    public Guid TenantId { get; private set; }
    public Guid NotificationId { get; private set; }
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? DeviceToken { get; private set; }
    public TemplateLanguage? Language { get; private set; }

    /// <summary>Returns the address this channel needs, or null if the recipient cannot be reached on it.</summary>
    public string? AddressFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => Email,
        NotificationChannel.Sms or NotificationChannel.WhatsApp or NotificationChannel.Voice => Phone,
        NotificationChannel.Push => DeviceToken,
        NotificationChannel.InApp or NotificationChannel.SignalR => UserId,
        _ => null
    };
}
