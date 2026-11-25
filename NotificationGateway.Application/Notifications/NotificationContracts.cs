using System.Text.Json;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Application.Notifications;

public sealed record RecipientRequest(string? UserId, string? Email, string? Phone, string? DeviceToken, TemplateLanguage? Language);

public sealed record SendNotificationRequest(
    string NotificationType,
    string TemplateCode,
    NotificationPriority Priority,
    JsonElement? Data,
    IReadOnlyList<RecipientRequest>? Recipients,   // nullable: the spec shape sends a single "recipient" instead
    IReadOnlyList<NotificationChannel>? Channels,
    string? IdempotencyKey,
    string? CorrelationId,
    string? TenantId = null,    // optional; when present it must match the authenticated tenant (never trusted on its own)
    RecipientRequest? Recipient = null);   // spec §5.A shape: a single "recipient" (merged into Recipients)

public sealed record DeliverySummaryDto(Guid DeliveryId, string? RecipientUserId, NotificationChannel Channel, DeliveryStatus Status, DateTimeOffset? NextAttemptAt);

public sealed record SendNotificationResponse(
    Guid NotificationId,
    bool Replayed,
    IReadOnlyList<DeliverySummaryDto> Deliveries,
    IReadOnlyList<string> Warnings);

public sealed record DeliveryAttemptDto(
    int AttemptNumber, string ProviderKey, Guid ProviderConfigurationId, AttemptOutcome? Outcome, string? ProviderMessageId,
    string? ErrorCode, string? ErrorMessage, int? HttpStatus, DateTimeOffset StartedAt, long? DurationMs);

public sealed record DeliveryDetailDto(
    Guid DeliveryId, Guid RecipientId, string? RecipientUserId, NotificationChannel Channel, DeliveryStatus Status, int AttemptCount,
    int MaxAttempts, string? LastProviderKey, string? ProviderMessageId, string? LastError, string? Address, string? Subject,
    TemplateLanguage? Language, int? TemplateVersion, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt,
    DateTimeOffset? NextAttemptAt, IReadOnlyList<DeliveryAttemptDto> Attempts);

public sealed record NotificationDetailDto(
    Guid Id, string NotificationType, string TemplateCode, NotificationPriority Priority, string? IdempotencyKey, string? CorrelationId,
    string? RequestedBy, DateTimeOffset CreatedAt, string OverallStatus, IReadOnlyList<DeliveryDetailDto> Deliveries);

public sealed record NotificationListItemDto(
    Guid Id, string NotificationType, string TemplateCode, NotificationPriority Priority, DateTimeOffset CreatedAt,
    int DeliveryCount, int DeliveredOrSent, int Failed, int Pending);

public sealed record NotificationQuery(
    string? NotificationType, DeliveryStatus? Status, NotificationChannel? Channel, DateTimeOffset? From, DateTimeOffset? To,
    string? ProviderKey = null, NotificationPriority? Priority = null, int Page = 1, int PageSize = 50);
