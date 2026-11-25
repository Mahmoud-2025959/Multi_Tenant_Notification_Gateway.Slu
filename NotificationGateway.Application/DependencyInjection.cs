using Microsoft.Extensions.DependencyInjection;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Application.Inbox;
using NotificationGateway.Application.Notifications;
using NotificationGateway.Application.Providers;
using NotificationGateway.Application.Reports;
using NotificationGateway.Application.Routing;
using NotificationGateway.Application.Settings;
using NotificationGateway.Application.Templates;
using NotificationGateway.Application.Webhooks;

namespace NotificationGateway.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton(new CircuitBreakerOptions());

        // Settings
        services.AddScoped<TenantAdminService>();
        services.AddScoped<ChannelSettingsService>();
        services.AddScoped<ProviderConfigurationService>();
        services.AddScoped<UserPreferenceService>();
        services.AddScoped<QuotaSettingsService>();
        services.AddScoped<RoutingRuleService>();

        // Templates + routing
        services.AddScoped<TemplateResolver>();
        services.AddScoped<TemplateService>();
        services.AddScoped<ChannelRouter>();

        // Notifications
        services.AddScoped<NotificationOrchestrator>();
        services.AddScoped<NotificationQueryService>();
        services.AddScoped<ProviderResolver>();
        services.AddScoped<TenantDirectory>();
        services.AddScoped<TenantNotificationSettingsService>();
        services.AddScoped<AdminReportService>();
        services.AddScoped<DeliveryProcessor>();
        services.AddScoped<WebhookService>();
        services.AddScoped<InboxService>();
        services.AddScoped<ReportService>();

        return services;
    }
}
