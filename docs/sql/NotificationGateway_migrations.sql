IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [TenantId] uniqueidentifier NULL,
        [UserId] nvarchar(128) NULL,
        [Action] nvarchar(32) NOT NULL,
        [EntityType] nvarchar(128) NOT NULL,
        [EntityId] nvarchar(128) NOT NULL,
        [ChangesJson] nvarchar(max) NULL,
        [At] datetimeoffset NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationProviders] (
        [Key] nvarchar(64) NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [DisplayName] nvarchar(128) NOT NULL,
        [Capabilities] int NOT NULL,
        [IsEnabled] bit NOT NULL,
        [EstimatedCostPerMessage] decimal(18,6) NULL,
        [CostCurrency] nvarchar(3) NULL,
        CONSTRAINT [PK_NotificationProviders] PRIMARY KEY ([Key])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Type] nvarchar(128) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [OccurredAt] datetimeoffset NOT NULL,
        [ProcessedAt] datetimeoffset NULL,
        [Attempts] int NOT NULL,
        [Error] nvarchar(1000) NULL,
        [LockedUntil] datetimeoffset NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [ProcessedWebhookEvents] (
        [Id] bigint NOT NULL IDENTITY,
        [ProviderKey] nvarchar(64) NOT NULL,
        [EventId] nvarchar(256) NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ReceivedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ProcessedWebhookEvents] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [Tenants] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [IsActive] bit NOT NULL,
        [DefaultLanguage] nvarchar(8) NOT NULL,
        [TimeZoneId] nvarchar(64) NOT NULL,
        [CriticalOverridesUserPreferences] bit NOT NULL,
        [QuotaExceededPolicy] nvarchar(16) NOT NULL,
        [RequestsPerMinute] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [InAppNotifications] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [UserId] nvarchar(128) NOT NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [Title] nvarchar(512) NULL,
        [Body] nvarchar(4000) NOT NULL,
        [NotificationType] nvarchar(128) NOT NULL,
        [IsRead] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ReadAt] datetimeoffset NULL,
        CONSTRAINT [PK_InAppNotifications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InAppNotifications_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationQuotas] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Channel] nvarchar(16) NULL,
        [Period] nvarchar(16) NOT NULL,
        [Limit] bigint NOT NULL,
        CONSTRAINT [PK_NotificationQuotas] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationQuotas_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationQuotaUsages] (
        [TenantId] uniqueidentifier NOT NULL,
        [ChannelKey] nvarchar(16) NOT NULL,
        [PeriodKey] nvarchar(10) NOT NULL,
        [Used] bigint NOT NULL,
        CONSTRAINT [PK_NotificationQuotaUsages] PRIMARY KEY ([TenantId], [ChannelKey], [PeriodKey]),
        CONSTRAINT [FK_NotificationQuotaUsages_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationRoutingRules] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [NotificationType] nvarchar(128) NULL,
        [MinPriority] int NULL,
        [Channels] nvarchar(256) NOT NULL,
        [Order] int NOT NULL,
        [AllowCallerOverride] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_NotificationRoutingRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationRoutingRules_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(128) NULL,
        [RequestHash] nvarchar(64) NULL,
        [NotificationType] nvarchar(128) NOT NULL,
        [TemplateCode] nvarchar(128) NOT NULL,
        [Priority] int NOT NULL,
        [DataJson] nvarchar(max) NOT NULL,
        [CorrelationId] nvarchar(128) NULL,
        [RequestedBy] nvarchar(128) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Notifications_TenantId_Id] UNIQUE ([TenantId], [Id]),
        CONSTRAINT [FK_Notifications_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationTemplates] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [Code] nvarchar(128) NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [Language] nvarchar(8) NOT NULL,
        [Version] int NOT NULL,
        [Subject] nvarchar(512) NULL,
        [Body] nvarchar(max) NOT NULL,
        [Placeholders] nvarchar(1000) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(128) NULL,
        CONSTRAINT [PK_NotificationTemplates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationTemplates_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationUserPreferences] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [UserId] nvarchar(128) NOT NULL,
        [Language] nvarchar(8) NULL,
        [DisabledChannels] nvarchar(256) NOT NULL,
        [DisabledNotificationTypes] nvarchar(2000) NOT NULL,
        [QuietHoursStart] time NULL,
        [QuietHoursEnd] time NULL,
        [TimeZoneId] nvarchar(64) NULL,
        [AllowCriticalOverride] bit NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_NotificationUserPreferences] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationUserPreferences_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [TenantChannelSettings] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [MaxAttempts] int NOT NULL,
        [BaseRetryDelaySeconds] int NOT NULL,
        [MaxRetryDelaySeconds] int NOT NULL,
        [ProviderTimeoutSeconds] int NOT NULL,
        CONSTRAINT [PK_TenantChannelSettings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TenantChannelSettings_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [TenantProviderConfigurations] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [ProviderKey] nvarchar(64) NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [SettingsJson] nvarchar(4000) NOT NULL,
        [EncryptedSecrets] nvarchar(4000) NOT NULL,
        [SecretVersion] int NOT NULL,
        [SecretsUpdatedAt] datetimeoffset NOT NULL,
        [SenderIdentity] nvarchar(256) NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_TenantProviderConfigurations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TenantProviderConfigurations_NotificationProviders_ProviderKey] FOREIGN KEY ([ProviderKey]) REFERENCES [NotificationProviders] ([Key]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TenantProviderConfigurations_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationRecipients] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [NotificationId] uniqueidentifier NOT NULL,
        [UserId] nvarchar(128) NULL,
        [Email] nvarchar(320) NULL,
        [Phone] nvarchar(32) NULL,
        [DeviceToken] nvarchar(512) NULL,
        [Language] nvarchar(8) NULL,
        CONSTRAINT [PK_NotificationRecipients] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_NotificationRecipients_TenantId_Id] UNIQUE ([TenantId], [Id]),
        CONSTRAINT [FK_NotificationRecipients_Notifications_TenantId_NotificationId] FOREIGN KEY ([TenantId], [NotificationId]) REFERENCES [Notifications] ([TenantId], [Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [ChannelProviderAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [ProviderConfigurationId] uniqueidentifier NOT NULL,
        [Order] int NOT NULL,
        CONSTRAINT [PK_ChannelProviderAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ChannelProviderAssignments_TenantProviderConfigurations_ProviderConfigurationId] FOREIGN KEY ([ProviderConfigurationId]) REFERENCES [TenantProviderConfigurations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ChannelProviderAssignments_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationWebhooks] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ProviderConfigurationId] uniqueidentifier NOT NULL,
        [ProviderKey] nvarchar(64) NOT NULL,
        [PublicToken] nvarchar(64) NOT NULL,
        [EncryptedSigningSecret] nvarchar(2000) NOT NULL,
        [PreviousEncryptedSigningSecret] nvarchar(2000) NULL,
        [PreviousSecretValidUntil] datetimeoffset NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_NotificationWebhooks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationWebhooks_TenantProviderConfigurations_ProviderConfigurationId] FOREIGN KEY ([ProviderConfigurationId]) REFERENCES [TenantProviderConfigurations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_NotificationWebhooks_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [ProviderHealth] (
        [ProviderConfigurationId] uniqueidentifier NOT NULL,
        [State] nvarchar(16) NOT NULL,
        [ConsecutiveFailures] int NOT NULL,
        [OpenUntil] datetimeoffset NULL,
        [LastSuccessAt] datetimeoffset NULL,
        [LastFailureAt] datetimeoffset NULL,
        [LastError] nvarchar(500) NULL,
        [TotalCalls] bigint NOT NULL,
        [TotalFailures] bigint NOT NULL,
        [TotalLatencyMs] bigint NOT NULL,
        CONSTRAINT [PK_ProviderHealth] PRIMARY KEY ([ProviderConfigurationId]),
        CONSTRAINT [FK_ProviderHealth_TenantProviderConfigurations_ProviderConfigurationId] FOREIGN KEY ([ProviderConfigurationId]) REFERENCES [TenantProviderConfigurations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [NotificationId] uniqueidentifier NOT NULL,
        [RecipientId] uniqueidentifier NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [Priority] int NOT NULL,
        [Status] int NOT NULL,
        [AttemptCount] int NOT NULL,
        [MaxAttempts] int NOT NULL,
        [ProviderIndex] int NOT NULL,
        [RetryRound] int NOT NULL,
        [NextAttemptAt] datetimeoffset NULL,
        [LockedBy] nvarchar(128) NULL,
        [LockedUntil] datetimeoffset NULL,
        [InFlightProviderConfigurationId] uniqueidentifier NULL,
        [LastProviderConfigurationId] uniqueidentifier NULL,
        [LastProviderKey] nvarchar(64) NULL,
        [ProviderMessageId] nvarchar(256) NULL,
        [LastError] nvarchar(1000) NULL,
        [TemplateId] uniqueidentifier NULL,
        [TemplateVersion] int NULL,
        [Language] nvarchar(8) NULL,
        [Address] nvarchar(512) NULL,
        [Subject] nvarchar(512) NULL,
        [Body] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [SentAt] datetimeoffset NULL,
        [DeliveredAt] datetimeoffset NULL,
        [CompletedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_NotificationDeliveries] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_NotificationDeliveries_TenantId_Id] UNIQUE ([TenantId], [Id]),
        CONSTRAINT [FK_NotificationDeliveries_NotificationRecipients_TenantId_RecipientId] FOREIGN KEY ([TenantId], [RecipientId]) REFERENCES [NotificationRecipients] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_NotificationDeliveries_Notifications_TenantId_NotificationId] FOREIGN KEY ([TenantId], [NotificationId]) REFERENCES [Notifications] ([TenantId], [Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationDeliveryAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [AttemptNumber] int NOT NULL,
        [ProviderConfigurationId] uniqueidentifier NOT NULL,
        [ProviderKey] nvarchar(64) NOT NULL,
        [Outcome] nvarchar(32) NULL,
        [ProviderMessageId] nvarchar(256) NULL,
        [ErrorCode] nvarchar(128) NULL,
        [ErrorMessage] nvarchar(1000) NULL,
        [HttpStatus] int NULL,
        [StartedAt] datetimeoffset NOT NULL,
        [CompletedAt] datetimeoffset NULL,
        [DurationMs] bigint NULL,
        CONSTRAINT [PK_NotificationDeliveryAttempts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationDeliveryAttempts_NotificationDeliveries_TenantId_DeliveryId] FOREIGN KEY ([TenantId], [DeliveryId]) REFERENCES [NotificationDeliveries] ([TenantId], [Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_TenantId_At] ON [AuditLogs] ([TenantId], [At]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ChannelProviderAssignments_ProviderConfigurationId] ON [ChannelProviderAssignments] ([ProviderConfigurationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ChannelProviderAssignments_TenantId_Channel_Order] ON [ChannelProviderAssignments] ([TenantId], [Channel], [Order]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ChannelProviderAssignments_TenantId_Channel_ProviderConfigurationId] ON [ChannelProviderAssignments] ([TenantId], [Channel], [ProviderConfigurationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InAppNotifications_DeliveryId] ON [InAppNotifications] ([DeliveryId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InAppNotifications_TenantId_UserId_IsRead_CreatedAt] ON [InAppNotifications] ([TenantId], [UserId], [IsRead], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Deliveries_Due] ON [NotificationDeliveries] ([Status], [NextAttemptAt]) INCLUDE ([TenantId], [Priority]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Deliveries_Lease] ON [NotificationDeliveries] ([Status], [LockedUntil]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Deliveries_ProviderMessage] ON [NotificationDeliveries] ([LastProviderConfigurationId], [ProviderMessageId]) WHERE [ProviderMessageId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationDeliveries_NotificationId_RecipientId_Channel] ON [NotificationDeliveries] ([NotificationId], [RecipientId], [Channel]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_TenantId_CreatedAt] ON [NotificationDeliveries] ([TenantId], [CreatedAt]) INCLUDE ([Channel], [Status], [LastProviderKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_TenantId_NotificationId] ON [NotificationDeliveries] ([TenantId], [NotificationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_TenantId_RecipientId] ON [NotificationDeliveries] ([TenantId], [RecipientId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationDeliveryAttempts_DeliveryId_AttemptNumber] ON [NotificationDeliveryAttempts] ([DeliveryId], [AttemptNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveryAttempts_TenantId_DeliveryId] ON [NotificationDeliveryAttempts] ([TenantId], [DeliveryId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveryAttempts_TenantId_StartedAt] ON [NotificationDeliveryAttempts] ([TenantId], [StartedAt]) INCLUDE ([ProviderKey], [Outcome], [DurationMs]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationQuotas_TenantId_Channel_Period] ON [NotificationQuotas] ([TenantId], [Channel], [Period]) WHERE [Channel] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRecipients_TenantId_NotificationId] ON [NotificationRecipients] ([TenantId], [NotificationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRecipients_TenantId_UserId] ON [NotificationRecipients] ([TenantId], [UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRoutingRules_TenantId_IsActive_Order] ON [NotificationRoutingRules] ([TenantId], [IsActive], [Order]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_TenantId_CreatedAt] ON [Notifications] ([TenantId], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Notifications_TenantId_IdempotencyKey] ON [Notifications] ([TenantId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_TenantId_NotificationType_CreatedAt] ON [Notifications] ([TenantId], [NotificationType], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationTemplates_Code_Channel_Language_IsActive] ON [NotificationTemplates] ([Code], [Channel], [Language], [IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationTemplates_TenantId_Code_Channel_Language_Version] ON [NotificationTemplates] ([TenantId], [Code], [Channel], [Language], [Version]) WHERE [TenantId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationUserPreferences_TenantId_UserId] ON [NotificationUserPreferences] ([TenantId], [UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationWebhooks_ProviderConfigurationId] ON [NotificationWebhooks] ([ProviderConfigurationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationWebhooks_PublicToken] ON [NotificationWebhooks] ([PublicToken]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationWebhooks_TenantId_ProviderConfigurationId] ON [NotificationWebhooks] ([TenantId], [ProviderConfigurationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Outbox_Pending] ON [OutboxMessages] ([ProcessedAt], [OccurredAt]) WHERE [ProcessedAt] IS NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProcessedWebhookEvents_ProviderKey_EventId] ON [ProcessedWebhookEvents] ([ProviderKey], [EventId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProcessedWebhookEvents_ReceivedAt] ON [ProcessedWebhookEvents] ([ReceivedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TenantChannelSettings_TenantId_Channel] ON [TenantChannelSettings] ([TenantId], [Channel]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TenantProviderConfigurations_ProviderKey] ON [TenantProviderConfigurations] ([ProviderKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TenantProviderConfigurations_TenantId_Channel_IsActive] ON [TenantProviderConfigurations] ([TenantId], [Channel], [IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Tenants_Code] ON [Tenants] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121193330_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261121193330_InitialCreate', N'8.0.34');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261122105950_AddPreferredChannels'
)
BEGIN
    ALTER TABLE [NotificationUserPreferences] ADD [PreferredChannels] nvarchar(256) NOT NULL DEFAULT N'';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261122105950_AddPreferredChannels'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261122105950_AddPreferredChannels', N'8.0.34');
END;
GO

COMMIT;
GO

