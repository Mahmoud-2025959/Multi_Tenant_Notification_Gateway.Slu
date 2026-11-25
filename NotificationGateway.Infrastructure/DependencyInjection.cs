using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Application.Providers;
using NotificationGateway.Application.Webhooks;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Infrastructure.Messaging;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Providers;
using NotificationGateway.Infrastructure.Security;
using NotificationGateway.Infrastructure.Tenancy;
using NotificationGateway.Infrastructure.Workers;

namespace NotificationGateway.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);

        // Tenancy
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // Persistence
        services.AddDbContext<NotificationGatewayDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("NotificationGateway"),
                sql =>
                {
                    sql.MigrationsAssembly(typeof(NotificationGatewayDbContext).Assembly.FullName);
                    sql.EnableRetryOnFailure(3);
                }));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<NotificationGatewayDbContext>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDeliveryLeaseStore, SqlDeliveryLeaseStore>();
        services.AddScoped<IQuotaCounter, SqlQuotaCounter>();
        services.AddScoped<IWebhookEventStore, SqlWebhookEventStore>();

        // Security
        services.AddOptions<SecretProtectionOptions>()
           .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
           .ValidateOnStart();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        // Resilience
        services.AddSingleton(configuration.GetSection("CircuitBreaker").Get<CircuitBreakerOptions>() ?? new CircuitBreakerOptions());

        // Messaging
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IMessageBusPublisher>(sp => sp.GetRequiredService<RabbitMqConnection>());
        services.AddSingleton<DispatchSignal>();
        services.AddSingleton<IDispatchSignal>(sp => sp.GetRequiredService<DispatchSignal>());

        // Outbound security (SSRF): endpoint validation at save time + IP check at connect time.
        services.Configure<OutboundSecurityOptions>(configuration.GetSection(OutboundSecurityOptions.SectionName));
        services.AddSingleton<IProviderEndpointValidator, ProviderEndpointValidator>();
        var allowPrivate = configuration.GetSection(OutboundSecurityOptions.SectionName).Get<OutboundSecurityOptions>()?.AllowPrivateNetworks ?? false;
        void ProviderHttpClient(string name) => services.AddHttpClient(name, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeHandler.Create(allowPrivate));

        // Provider adapters: adding a provider = one line here + one class. Nothing else changes.
        ProviderHttpClient(SendGridProvider.HttpClientName);
        ProviderHttpClient(TwilioSmsProvider.HttpClientName);
        ProviderHttpClient(MetaWhatsAppProvider.HttpClientName);
        ProviderHttpClient(LocalSmsGatewayProvider.HttpClientName);
        ProviderHttpClient(OneSignalPushProvider.HttpClientName);
        services.AddSingleton<INotificationProvider, InAppProvider>();
        services.AddSingleton<INotificationProvider, SignalRProvider>();
        services.AddSingleton<INotificationProvider, SmtpProvider>();
        services.AddSingleton<INotificationProvider, SendGridProvider>();
        services.AddSingleton<INotificationProvider, TwilioSmsProvider>();
        services.AddSingleton<INotificationProvider, LocalSmsGatewayProvider>();
        services.AddSingleton<INotificationProvider, MetaWhatsAppProvider>();
        services.AddSingleton<INotificationProvider, TwilioWhatsAppProvider>();
        services.AddSingleton<INotificationProvider, OneSignalPushProvider>();
        services.AddSingleton<INotificationProvider, TwilioVoiceProvider>();
        foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Push, NotificationChannel.Voice })
        {
            services.AddSingleton<INotificationProvider>(new FakeProvider(channel));
            services.AddSingleton<IProviderWebhookHandler>(new FakeWebhookHandler(channel));
        }
        services.AddSingleton<IProviderWebhookHandler>(new TwilioWebhookHandler(TwilioSmsProvider.Key));
        services.AddSingleton<IProviderWebhookHandler>(new TwilioWebhookHandler(TwilioWhatsAppProvider.Key));
        services.AddSingleton<IProviderWebhookHandler, MetaWhatsAppWebhookHandler>();
        services.AddSingleton<IProviderWebhookHandler, SendGridWebhookHandler>();

        // Background processing
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.AddHostedService<ProviderCatalogSync>();
        var workers = configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
        if (workers.Enabled)
        {
            services.AddHostedService<OutboxRelayWorker>();
            services.AddHostedService<RabbitMqWakeupConsumer>();
            services.AddHostedService<DispatcherWorker>();
            services.AddHostedService<LeaseRecoveryWorker>();
        }

        return services;
    }
}
