using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationGateway.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationProviders",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Capabilities = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EstimatedCostPerMessage = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    CostCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationProviders", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DefaultLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CriticalOverridesUserPreferences = table.Column<bool>(type: "bit", nullable: false),
                    QuotaExceededPolicy = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InAppNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InAppNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InAppNotifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Period = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Limit = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationQuotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationQuotas_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationQuotaUsages",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Used = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationQuotaUsages", x => new { x.TenantId, x.ChannelKey, x.PeriodKey });
                    table.ForeignKey(
                        name: "FK_NotificationQuotaUsages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRoutingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MinPriority = table.Column<int>(type: "int", nullable: true),
                    Channels = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    AllowCallerOverride = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRoutingRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NotificationType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TemplateCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.UniqueConstraint("AK_Notifications_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Notifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    Placeholders = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationUserPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    DisabledChannels = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisabledNotificationTypes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    QuietHoursStart = table.Column<TimeOnly>(type: "time", nullable: true),
                    QuietHoursEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AllowCriticalOverride = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationUserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationUserPreferences_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantChannelSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    BaseRetryDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    MaxRetryDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    ProviderTimeoutSeconds = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantChannelSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantChannelSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantProviderConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EncryptedSecrets = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SecretVersion = table.Column<int>(type: "int", nullable: false),
                    SecretsUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SenderIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantProviderConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantProviderConfigurations_NotificationProviders_ProviderKey",
                        column: x => x.ProviderKey,
                        principalTable: "NotificationProviders",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantProviderConfigurations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    DeviceToken = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRecipients", x => x.Id);
                    table.UniqueConstraint("AK_NotificationRecipients_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_NotificationRecipients_Notifications_TenantId_NotificationId",
                        columns: x => new { x.TenantId, x.NotificationId },
                        principalTable: "Notifications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChannelProviderAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelProviderAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelProviderAssignments_TenantProviderConfigurations_ProviderConfigurationId",
                        column: x => x.ProviderConfigurationId,
                        principalTable: "TenantProviderConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelProviderAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationWebhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PublicToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EncryptedSigningSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreviousEncryptedSigningSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PreviousSecretValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationWebhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationWebhooks_TenantProviderConfigurations_ProviderConfigurationId",
                        column: x => x.ProviderConfigurationId,
                        principalTable: "TenantProviderConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationWebhooks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderHealth",
                columns: table => new
                {
                    ProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    OpenUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotalCalls = table.Column<long>(type: "bigint", nullable: false),
                    TotalFailures = table.Column<long>(type: "bigint", nullable: false),
                    TotalLatencyMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderHealth", x => x.ProviderConfigurationId);
                    table.ForeignKey(
                        name: "FK_ProviderHealth_TenantProviderConfigurations_ProviderConfigurationId",
                        column: x => x.ProviderConfigurationId,
                        principalTable: "TenantProviderConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    ProviderIndex = table.Column<int>(type: "int", nullable: false),
                    RetryRound = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InFlightProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastProviderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateVersion = table.Column<int>(type: "int", nullable: true),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.UniqueConstraint("AK_NotificationDeliveries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationRecipients_TenantId_RecipientId",
                        columns: x => new { x.TenantId, x.RecipientId },
                        principalTable: "NotificationRecipients",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_Notifications_TenantId_NotificationId",
                        columns: x => new { x.TenantId, x.NotificationId },
                        principalTable: "Notifications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    ProviderConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveryAttempts_NotificationDeliveries_TenantId_DeliveryId",
                        columns: x => new { x.TenantId, x.DeliveryId },
                        principalTable: "NotificationDeliveries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_At",
                table: "AuditLogs",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelProviderAssignments_ProviderConfigurationId",
                table: "ChannelProviderAssignments",
                column: "ProviderConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelProviderAssignments_TenantId_Channel_Order",
                table: "ChannelProviderAssignments",
                columns: new[] { "TenantId", "Channel", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelProviderAssignments_TenantId_Channel_ProviderConfigurationId",
                table: "ChannelProviderAssignments",
                columns: new[] { "TenantId", "Channel", "ProviderConfigurationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_DeliveryId",
                table: "InAppNotifications",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_TenantId_UserId_IsRead_CreatedAt",
                table: "InAppNotifications",
                columns: new[] { "TenantId", "UserId", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Due",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "NextAttemptAt" })
                .Annotation("SqlServer:Include", new[] { "TenantId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Lease",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "LockedUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ProviderMessage",
                table: "NotificationDeliveries",
                columns: new[] { "LastProviderConfigurationId", "ProviderMessageId" },
                filter: "[ProviderMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_NotificationId_RecipientId_Channel",
                table: "NotificationDeliveries",
                columns: new[] { "NotificationId", "RecipientId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_TenantId_CreatedAt",
                table: "NotificationDeliveries",
                columns: new[] { "TenantId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "Channel", "Status", "LastProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_TenantId_NotificationId",
                table: "NotificationDeliveries",
                columns: new[] { "TenantId", "NotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_TenantId_RecipientId",
                table: "NotificationDeliveries",
                columns: new[] { "TenantId", "RecipientId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_DeliveryId_AttemptNumber",
                table: "NotificationDeliveryAttempts",
                columns: new[] { "DeliveryId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_TenantId_DeliveryId",
                table: "NotificationDeliveryAttempts",
                columns: new[] { "TenantId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryAttempts_TenantId_StartedAt",
                table: "NotificationDeliveryAttempts",
                columns: new[] { "TenantId", "StartedAt" })
                .Annotation("SqlServer:Include", new[] { "ProviderKey", "Outcome", "DurationMs" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationQuotas_TenantId_Channel_Period",
                table: "NotificationQuotas",
                columns: new[] { "TenantId", "Channel", "Period" },
                unique: true,
                filter: "[Channel] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_TenantId_NotificationId",
                table: "NotificationRecipients",
                columns: new[] { "TenantId", "NotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_TenantId_UserId",
                table: "NotificationRecipients",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoutingRules_TenantId_IsActive_Order",
                table: "NotificationRoutingRules",
                columns: new[] { "TenantId", "IsActive", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_CreatedAt",
                table: "Notifications",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_IdempotencyKey",
                table: "Notifications",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_NotificationType_CreatedAt",
                table: "Notifications",
                columns: new[] { "TenantId", "NotificationType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_Code_Channel_Language_IsActive",
                table: "NotificationTemplates",
                columns: new[] { "Code", "Channel", "Language", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_TenantId_Code_Channel_Language_Version",
                table: "NotificationTemplates",
                columns: new[] { "TenantId", "Code", "Channel", "Language", "Version" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationUserPreferences_TenantId_UserId",
                table: "NotificationUserPreferences",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationWebhooks_ProviderConfigurationId",
                table: "NotificationWebhooks",
                column: "ProviderConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationWebhooks_PublicToken",
                table: "NotificationWebhooks",
                column: "PublicToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationWebhooks_TenantId_ProviderConfigurationId",
                table: "NotificationWebhooks",
                columns: new[] { "TenantId", "ProviderConfigurationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Pending",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "OccurredAt" },
                filter: "[ProcessedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_ProviderKey_EventId",
                table: "ProcessedWebhookEvents",
                columns: new[] { "ProviderKey", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_ReceivedAt",
                table: "ProcessedWebhookEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TenantChannelSettings_TenantId_Channel",
                table: "TenantChannelSettings",
                columns: new[] { "TenantId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantProviderConfigurations_ProviderKey",
                table: "TenantProviderConfigurations",
                column: "ProviderKey");

            migrationBuilder.CreateIndex(
                name: "IX_TenantProviderConfigurations_TenantId_Channel_IsActive",
                table: "TenantProviderConfigurations",
                columns: new[] { "TenantId", "Channel", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                table: "Tenants",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ChannelProviderAssignments");

            migrationBuilder.DropTable(
                name: "InAppNotifications");

            migrationBuilder.DropTable(
                name: "NotificationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "NotificationQuotas");

            migrationBuilder.DropTable(
                name: "NotificationQuotaUsages");

            migrationBuilder.DropTable(
                name: "NotificationRoutingRules");

            migrationBuilder.DropTable(
                name: "NotificationTemplates");

            migrationBuilder.DropTable(
                name: "NotificationUserPreferences");

            migrationBuilder.DropTable(
                name: "NotificationWebhooks");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "ProcessedWebhookEvents");

            migrationBuilder.DropTable(
                name: "ProviderHealth");

            migrationBuilder.DropTable(
                name: "TenantChannelSettings");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "TenantProviderConfigurations");

            migrationBuilder.DropTable(
                name: "NotificationRecipients");

            migrationBuilder.DropTable(
                name: "NotificationProviders");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
