using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Providers;
using Xunit;

namespace NotificationGateway.IntegrationTests;

/// <summary>Security boundaries beyond T01–T18: authentication, tenant resolution, secrets, audit, callbacks.</summary>
[Collection(GatewayCollection.Name)]
public sealed class TenantIsolationAndSettingsTests(GatewayFactory factory)
{
    [Fact(DisplayName = "SEC Request without a token is 401")]
    public async Task NoToken() =>
        (await factory.CreateClient().GetAsync("/api/tenants/tenant-001/channels")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact(DisplayName = "SEC Token without a tenant is 403 for non-admins")]
    public async Task NoTenantClaim() =>
        (await factory.ClientFor(null, "someone", Roles.TenantAdmin).GetAsync("/api/notifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact(DisplayName = "SEC Token for an unknown tenant is 403")]
    public async Task UnknownTenant() =>
        (await factory.ClientFor(Guid.NewGuid(), "someone", Roles.TenantAdmin).GetAsync("/api/notifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact(DisplayName = "SEC Tenant user cannot call tenant-admin or send endpoints")]
    public async Task TenantUserIsNotAdmin()
    {
        var tenant = await factory.CreateTenantAsync();
        (await tenant.User("u1").GetAsync($"{tenant.Base}/providers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tenant.User("u1").PostAsJsonAsync("/api/notifications/send", TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "SEC Client-supplied tenantId in the body must match the token")]
    public async Task BodyTenantIdIsValidated()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await a.EnableInAppAsync();

        var body = new
        {
            tenantId = b.Code, notificationType = "attendance.late", templateCode = "ATTENDANCE_LATE", priority = "Normal",
            data = new { EmployeeName = "X", CheckInTime = "08:00" }, recipient = new { userId = "u1" }, channels = new[] { "InApp" }
        };
        (await a.SendAsync(body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await a.SendAsync(body with { tenantId = a.Code })).StatusCode.Should().Be(HttpStatusCode.Accepted);   // spec shape: single "recipient"
    }

    [Fact(DisplayName = "SEC SystemAdmin can act for a tenant through the route; other tenant admins cannot")]
    public async Task AdminActsForTenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        (await factory.SystemAdmin().GetAsync($"/api/tenants/{a.Code}/notification-settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await b.Admin.GetAsync($"/api/tenants/{a.Code}/notification-settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "SEC Secrets are write-only")]
    public async Task SecretsWriteOnly()
    {
        var tenant = await factory.CreateTenantAsync();
        const string secret = "SG.super-secret-api-key-value";

        var created = await tenant.Admin.PostAsJsonAsync($"{tenant.Base}/providers", new
        {
            providerKey = SendGridProvider.Key, name = "SendGrid", settings = new { from = "noreply@example.com" },
            secrets = new Dictionary<string, string> { ["apiKey"] = secret }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await created.Content.ReadAsStringAsync();
        body.Should().NotContain(secret).And.Contain("apiKey");   // only the NAME is exposed

        (await tenant.Admin.GetStringAsync($"{tenant.Base}/notification-settings")).Should().NotContain(secret);
    }

    [Fact(DisplayName = "SEC Provider configuration is validated by its adapter")]
    public async Task AdapterValidation()
    {
        var tenant = await factory.CreateTenantAsync();
        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Base}/providers",
            new { providerKey = TwilioSmsProvider.Key, name = "Twilio", settings = new { }, secrets = new Dictionary<string, string>() });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("accountSid").And.Contain("authToken");
    }

    [Fact(DisplayName = "SEC Tenant admin cannot create shared configurations or set quotas")]
    public async Task PlatformOnlyOperations()
    {
        var tenant = await factory.CreateTenantAsync();
        (await tenant.Admin.PostAsJsonAsync($"{tenant.Base}/providers",
                new { providerKey = FakeProvider.KeyFor(NotificationChannel.Sms), name = "shared?", settings = new { }, shared = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tenant.Admin.PutAsJsonAsync($"{tenant.Base}/quotas", new { period = "Daily", limit = 1_000_000 }, GatewayFactory.Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "SEC Configuration changes are audited without secret values")]
    public async Task Audit()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.AddProviderAsync(SendGridProvider.Key, new { from = "a@b.com" }, new Dictionary<string, string> { ["apiKey"] = "SG.audit-me" });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
        var logs = db.AuditLogs.Where(l => l.TenantId == tenant.Id && l.EntityType == "TenantProviderConfiguration").ToList();
        logs.Should().NotBeEmpty();
        logs.Should().OnlyContain(l => l.ChangesJson == null || !l.ChangesJson.Contains("SG.audit-me"));
    }

    [Fact(DisplayName = "SEC A valid webhook of tenant B cannot update tenant A's delivery (callback ownership)")]
    public async Task WebhookOwnership()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await a.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "success" });
        var bConfig = (await b.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "success" }))[0];

        var (aId, _) = await TestTenant.ReadAccepted(await a.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, new { phone = "+201001234567" })));
        await factory.DispatchDueAsync();
        var aMessageId = TestTenant.DeliveryOf(await a.GetNotificationAsync(aId), "Sms").GetProperty("providerMessageId").GetString()!;

        var hook = await (await b.Admin.PostAsJsonAsync($"{b.Base}/webhooks", new { providerConfigurationId = bConfig }))
            .Content.ReadFromJsonAsync<JsonElement>(GatewayFactory.Json);
        var payload = JsonSerializer.Serialize(new { events = new[] { new { eventId = "evt-" + Guid.NewGuid(), providerMessageId = aMessageId, status = "failed" } } });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, hook.GetProperty("callbackPath").GetString())
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Fake-Signature", FakeWebhookHandler.Sign(hook.GetProperty("signingSecret").GetString()!, payload, now));
        request.Headers.Add("X-Fake-Timestamp", now.ToString());

        var result = await (await factory.CreateClient().SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("unmatched").GetInt32().Should().Be(1);
        TestTenant.StatusOf(await a.GetNotificationAsync(aId), "Sms").Should().Be("Sent");
    }

    [Fact(DisplayName = "SEC Unknown webhook token is 404")]
    public async Task UnknownWebhookToken() =>
        (await factory.CreateClient().PostAsync($"/api/webhooks/{FakeProvider.KeyFor(NotificationChannel.Sms)}/not-a-real-token",
            new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
}
