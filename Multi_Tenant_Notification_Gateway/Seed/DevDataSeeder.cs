using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Domain.Tenants;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Providers;
using NotificationGateway.Infrastructure.Workers;

namespace Multi_Tenant_Notification_Gateway.Seed;

/// <summary>
/// Development demo data. Shows the two tenants using DIFFERENT providers through the same engine:
///   tenant-001: SMS primary fails (fake "transient") → falls back to a backup SMS provider; e-mail via shared SMTP (smtp4dev).
///   tenant-002: e-mail via its own fake e-mail provider, SMS via its own fake SMS provider, Arabic SMS template override.
/// </summary>
public static class DevDataSeeder
{
    public static readonly Guid Tenant1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Tenant2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task SeedAsync(IServiceProvider root, CancellationToken ct = default)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<NotificationGatewayDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var protector = sp.GetRequiredService<ISecretProtector>();
        var now = DateTimeOffset.UtcNow;

        await ProviderCatalogSync.SyncAsync(sp, ct);

        using var tenantScope = tenant.BeginSystemScope();
        if (await db.Tenants.AnyAsync(ct)) return;

        var t1 = new Tenant("tenant-001", "Central Agency for Reconstruction - HQ", TemplateLanguage.Ar, "Africa/Cairo");
        var t2 = new Tenant("tenant-002", "Central Agency for Reconstruction - Branch", TemplateLanguage.En, "Africa/Cairo");
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(t1, Tenant1);
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(t2, Tenant2);
        db.Tenants.AddRange(t1, t2);

        TenantProviderConfiguration Config(Guid? owner, string key, NotificationChannel channel, string name, object settings, object? secrets = null, string? sender = null)
        {
            var c = new TenantProviderConfiguration(owner, key, channel, name, JsonSerializer.Serialize(settings), "pending", sender, now);
            c.ReplaceEnvelope(protector.Protect(JsonSerializer.Serialize(secrets ?? new Dictionary<string, string>()), SecretBindings.ProviderConfig(c.Id)));
            db.ProviderConfigurations.Add(c);
            db.ProviderHealth.Add(new ProviderHealth(c.Id));
            return c;
        }

        // ---- shared (platform-owned) configurations
        var sharedSmtp = Config(null, SmtpProvider.Key, NotificationChannel.Email, "Platform SMTP (smtp4dev)",
            new { host = "localhost", port = 2525, security = "none" }, sender: "no-reply@gateway.local");
        var sharedInApp = Config(null, InAppProvider.Key, NotificationChannel.InApp, "In-app inbox", new { });
        var sharedSignalR = Config(null, SignalRProvider.Key, NotificationChannel.SignalR, "SignalR", new { });

        // ---- tenant-001
        var t1SmsPrimary = Config(Tenant1, FakeProvider.KeyFor(NotificationChannel.Sms), NotificationChannel.Sms, "Primary SMS (simulated outage)",
            new { mode = "transient" }, sender: "AGENCY");
        var t1SmsBackup = Config(Tenant1, FakeProvider.KeyFor(NotificationChannel.Sms), NotificationChannel.Sms, "Backup SMS", new { mode = "success" }, sender: "AGENCY");

        // ---- tenant-002
        var t2Email = Config(Tenant2, FakeProvider.KeyFor(NotificationChannel.Email), NotificationChannel.Email, "Branch e-mail", new { mode = "success" },
            sender: "branch@gateway.local");
        var t2Sms = Config(Tenant2, FakeProvider.KeyFor(NotificationChannel.Sms), NotificationChannel.Sms, "Branch SMS", new { mode = "success" }, sender: "BRANCH");

        foreach (var (tenantId, chains) in new[]
                 {
                     (Tenant1, new Dictionary<NotificationChannel, TenantProviderConfiguration[]>
                     {
                         [NotificationChannel.InApp] = new[] { sharedInApp },
                         [NotificationChannel.SignalR] = new[] { sharedSignalR },
                         [NotificationChannel.Email] = new[] { sharedSmtp },
                         [NotificationChannel.Sms] = new[] { t1SmsPrimary, t1SmsBackup }
                     }),
                     (Tenant2, new Dictionary<NotificationChannel, TenantProviderConfiguration[]>
                     {
                         [NotificationChannel.InApp] = new[] { sharedInApp },
                         [NotificationChannel.SignalR] = new[] { sharedSignalR },
                         [NotificationChannel.Email] = new[] { t2Email },
                         [NotificationChannel.Sms] = new[] { t2Sms }
                     })
                 })
        {
            foreach (var (channel, configs) in chains)
            {
                var setting = new TenantChannelSetting(tenantId, channel, true);
                setting.Update(true, maxAttempts: 4, baseRetryDelaySeconds: 5, maxRetryDelaySeconds: 300, providerTimeoutSeconds: 15);
                db.ChannelSettings.Add(setting);
                for (var i = 0; i < configs.Length; i++)
                    db.ChannelProviderAssignments.Add(new ChannelProviderAssignment(tenantId, channel, configs[i].Id, i));
            }

            db.RoutingRules.Add(new NotificationRoutingRule(tenantId, "Critical → every channel", null, NotificationPriority.Critical,
                new[] { NotificationChannel.InApp, NotificationChannel.SignalR, NotificationChannel.Email, NotificationChannel.Sms }, 10, false));
            db.RoutingRules.Add(new NotificationRoutingRule(tenantId, "Late arrival", "attendance.late", null,
                new[] { NotificationChannel.InApp, NotificationChannel.SignalR, NotificationChannel.Email }, 20, true));
            db.RoutingRules.Add(new NotificationRoutingRule(tenantId, "Default → in-app", null, null,
                new[] { NotificationChannel.InApp }, 1000, true));
        }

        // ---- shared templates (EN + AR) + one tenant override
        void Template(Guid? owner, NotificationChannel channel, TemplateLanguage lang, string? subject, string body) =>
            db.Templates.Add(new NotificationTemplate(owner, "ATTENDANCE_LATE", channel, lang, 1, subject, body, now, "seed"));

        foreach (var channel in new[] { NotificationChannel.InApp, NotificationChannel.SignalR })
        {
            Template(null, channel, TemplateLanguage.En, "Late arrival", "{{EmployeeName}} checked in late at {{CheckInTime}}.");
            Template(null, channel, TemplateLanguage.Ar, "تأخير في الحضور", "قام {{EmployeeName}} بتسجيل الحضور متأخراً الساعة {{CheckInTime}}.");
        }
        Template(null, NotificationChannel.Email, TemplateLanguage.En, "Late arrival: {{EmployeeName}}",
            "<p>Dear manager,</p><p><b>{{EmployeeName}}</b> checked in late at {{CheckInTime}} on {{Date}}.</p>");
        Template(null, NotificationChannel.Email, TemplateLanguage.Ar, "تأخير في الحضور: {{EmployeeName}}",
            "<p dir=\"rtl\">السيد المدير،</p><p dir=\"rtl\">قام <b>{{EmployeeName}}</b> بتسجيل الحضور متأخراً الساعة {{CheckInTime}} بتاريخ {{Date}}.</p>");
        Template(null, NotificationChannel.Sms, TemplateLanguage.En, null, "{{EmployeeName}} late check-in {{CheckInTime}}.");
        Template(null, NotificationChannel.Sms, TemplateLanguage.Ar, null, "تأخير: {{EmployeeName}} الساعة {{CheckInTime}}");
        Template(Tenant2, NotificationChannel.Sms, TemplateLanguage.Ar, null, "فرع: تأخر {{EmployeeName}} ({{CheckInTime}})");

        await db.SaveChangesAsync(ct);
    }
}
