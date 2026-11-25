using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Providers;
using Xunit;

namespace NotificationGateway.IntegrationTests;

/// <summary>
/// The 18 mandatory scenarios of Task 8 §8, one test per ID (DisplayName starts with the ID).
/// Every test creates its own tenant(s) through the API, so they are independent and prove isolation as a side effect.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class SpecScenarioTests(GatewayFactory factory)
{
    private static readonly string FakeSms = FakeProvider.KeyFor(NotificationChannel.Sms);
    private static readonly string FakeEmail = FakeProvider.KeyFor(NotificationChannel.Email);
    private static readonly object SmsRecipient = new { userId = "emp-1", phone = "+201001234567" };

    private static NotificationMessage? SentBy(Guid deliveryId) => FakeProvider.Sent.LastOrDefault(m => m.DeliveryId == deliveryId);

    // ------------------------------------------------------------------------------------------------ T01

    [Fact(DisplayName = "T01 Send In-App notification: stored and accessible to the recipient only")]
    public async Task T01_InApp()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableInAppAsync();

        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "manager-1" })));
        await factory.DispatchDueAsync();

        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "InApp").Should().Be("Delivered");

        var inbox = await tenant.User("manager-1").GetFromJsonAsync<JsonElement>("/api/me/inbox", GatewayFactory.Json);
        inbox.GetProperty("items")[0].GetProperty("body").GetString().Should().Contain("Ahmed Ali").And.Contain("09:42");
        (await tenant.User("manager-1").GetFromJsonAsync<JsonElement>("/api/me/inbox/unread-count")).GetProperty("count").GetInt32().Should().Be(1);

        var someoneElse = await tenant.User("other-user").GetFromJsonAsync<JsonElement>("/api/me/inbox", GatewayFactory.Json);
        someoneElse.GetProperty("total").GetInt32().Should().Be(0);
    }

    // ------------------------------------------------------------------------------------------------ T02

    [Fact(DisplayName = "T02 Send SignalR notification: only the correct connected user receives it")]
    public async Task T02_SignalR()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableSharedAsync("SignalR", SignalRProvider.Key);
        var otherTenant = await factory.CreateTenantAsync();

        async Task<(HubConnection Connection, TaskCompletionSource<JsonElement> Received)> Connect(Guid tenantId, string userId)
        {
            var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/notifications"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    o.AccessTokenProvider = () => Task.FromResult<string?>(factory.Token(tenantId, userId, "TenantUser"));
                    o.Transports = HttpTransportType.LongPolling;
                })
                .Build();
            connection.On<JsonElement>("notification", m => received.TrySetResult(m));
            await connection.StartAsync();
            return (connection, received);
        }

        var target = await Connect(tenant.Id, "manager-1");
        var sameTenantOtherUser = await Connect(tenant.Id, "manager-2");
        var otherTenantSameUserId = await Connect(otherTenant.Id, "manager-1");   // same user id, different tenant

        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "SignalR" }, new { userId = "manager-1" })));
        await factory.DispatchDueAsync();

        var message = await target.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        message.GetProperty("body").GetString().Should().Contain("Ahmed Ali");
        message.GetProperty("notificationId").GetGuid().Should().Be(id);

        await Task.Delay(1500);
        sameTenantOtherUser.Received.Task.IsCompleted.Should().BeFalse();
        otherTenantSameUserId.Received.Task.IsCompleted.Should().BeFalse();

        // SignalR cannot prove the client displayed it: Sent, never Delivered.
        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "SignalR").Should().Be("Sent");

        foreach (var c in new[] { target.Connection, sameTenantOtherUser.Connection, otherTenantSameUserId.Connection }) await c.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------------------ T03 / T04

    [Fact(DisplayName = "T03 Send Email using provider A: provider A is invoked")]
    public async Task T03_EmailProviderA()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableChannelAsync("Email");
        var providerA = await tenant.AddProviderAsync(FakeEmail, new { mode = "success" }, name: "Provider A");
        await tenant.SetChainAsync("Email", providerA);

        var (id, deliveries) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Email" }, new { email = "manager@agency.gov.eg" })));
        await factory.DispatchDueAsync();

        var delivery = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Email");
        delivery.GetProperty("status").GetString().Should().Be("Sent");
        delivery.GetProperty("attempts")[0].GetProperty("providerConfigurationId").GetGuid().Should().Be(providerA);
        SentBy(deliveries["Email"])!.Configuration.ConfigurationId.Should().Be(providerA);
    }

    [Fact(DisplayName = "T04 Switch Email to provider B (SendGrid): configuration only, no code change")]
    public async Task T04_SwitchEmailProvider()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableChannelAsync("Email");
        var providerA = await tenant.AddProviderAsync(FakeEmail, new { mode = "success" }, name: "Provider A");
        await tenant.SetChainAsync("Email", providerA);

        // The switch is two API calls: register provider B and make it the primary. Nothing is redeployed.
        var providerB = await tenant.AddProviderAsync(SendGridProvider.Key, new { from = "noreply@agency.gov.eg", baseUrl = "https://sendgrid.test" },
            new Dictionary<string, string> { ["apiKey"] = "SG.tenant-key" }, "Provider B (SendGrid)");
        await tenant.SetChainAsync("Email", providerB);

        var before = GatewayFactory.SendGridStub.Requests.Count;
        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Email" }, new { email = "manager@agency.gov.eg" })));
        await factory.DispatchDueAsync();

        var delivery = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Email");
        delivery.GetProperty("status").GetString().Should().Be("Sent");
        delivery.GetProperty("lastProviderKey").GetString().Should().Be(SendGridProvider.Key);
        delivery.GetProperty("providerMessageId").GetString().Should().Be("sg-stub-message");

        GatewayFactory.SendGridStub.Requests.Count.Should().Be(before + 1);
        var call = GatewayFactory.SendGridStub.Requests.Last();
        call.Request.Headers.Authorization!.Parameter.Should().Be("SG.tenant-key");   // tenant's own decrypted credential
        call.Body.Should().Contain("manager@agency.gov.eg").And.Contain("Ahmed Ali");
    }

    // ------------------------------------------------------------------------------------------------ T05 / T06

    [Fact(DisplayName = "T05 Primary provider fails: the configured fallback is attempted")]
    public async Task T05_Fallback()
    {
        var tenant = await factory.CreateTenantAsync();
        var ids = await tenant.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "transient" }, new { mode = "success" });

        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();

        var delivery = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Sms");
        delivery.GetProperty("status").GetString().Should().Be("Sent");
        var attempts = delivery.GetProperty("attempts").EnumerateArray().ToList();
        attempts.Should().HaveCount(2);
        attempts[0].GetProperty("providerConfigurationId").GetGuid().Should().Be(ids[0]);
        attempts[0].GetProperty("outcome").GetString().Should().Be("TransientFailure");
        attempts[1].GetProperty("providerConfigurationId").GetGuid().Should().Be(ids[1]);
        attempts[1].GetProperty("outcome").GetString().Should().Be("Accepted");
    }

    [Fact(DisplayName = "T06 All providers fail: retried with backoff and eventually dead-lettered")]
    public async Task T06_RetryThenDeadLetter()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.UseFakeChainAsync(NotificationChannel.Sms, 3, new { mode = "transient" });

        var (id, deliveries) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));

        await factory.DispatchDueAsync();
        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "Sms").Should().Be("Retrying");

        (await factory.DispatchUntilAsync(tenant, id, "Sms", TimeSpan.FromSeconds(20), "DeadLettered")).Should().Be("DeadLettered");

        var delivery = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Sms");
        delivery.GetProperty("attemptCount").GetInt32().Should().Be(3);
        delivery.GetProperty("attempts").GetArrayLength().Should().Be(3);

        var deadLetters = await tenant.Admin.GetFromJsonAsync<JsonElement>($"{tenant.Base}/dead-letters", GatewayFactory.Json);
        deadLetters.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("deliveryId").GetGuid()).Should().Contain(deliveries["Sms"]);

        // Replay from the dead-letter queue.
        (await tenant.Admin.PostAsync($"{tenant.Base}/dead-letters/{deliveries["Sms"]}/requeue", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "Sms").Should().Be("Queued");
    }

    // ------------------------------------------------------------------------------------------------ T07 / T08

    [Fact(DisplayName = "T07 Tenant A requests Tenant B history: access is denied")]
    public async Task T07_HistoryIsolation()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await b.EnableInAppAsync();
        var (bNotification, _) = await TestTenant.ReadAccepted(await b.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })));

        (await a.Sender.GetAsync($"/api/tenants/{b.Code}/notifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await a.Sender.GetAsync($"/api/tenants/{b.Id}/notifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await a.Sender.GetAsync($"/api/notifications/{bNotification}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Admin.GetAsync($"/api/tenants/{b.Code}/reports/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var own = await a.Sender.GetFromJsonAsync<JsonElement>("/api/notifications", GatewayFactory.Json);
        own.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).Should().NotContain(bNotification);
    }

    [Fact(DisplayName = "T08 Tenant A uses Tenant B provider ID: request is rejected")]
    public async Task T08_ForeignProviderId()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await a.EnableChannelAsync("Sms");
        var bConfig = await b.AddProviderAsync(FakeSms, new { mode = "success" });

        (await a.Admin.PutAsJsonAsync($"{a.Base}/channels/Sms/providers", new { providerConfigurationIds = new[] { bConfig } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Admin.GetAsync($"{a.Base}/providers/{bConfig}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Admin.PutAsJsonAsync($"{a.Base}/providers/{bConfig}", new { name = "hijack", settings = new { mode = "success" }, isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Admin.PostAsJsonAsync($"{a.Base}/webhooks", new { providerConfigurationId = bConfig }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Admin.DeleteAsync($"{a.Base}/providers/{bConfig}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------------------------ T09

    [Fact(DisplayName = "T09 Tenant-specific templates: the tenant's own template is selected")]
    public async Task T09_TenantTemplate()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await a.EnableInAppAsync();
        await b.EnableInAppAsync();

        var created = await a.Admin.PostAsJsonAsync($"{a.Base}/templates", new
        {
            code = "ATTENDANCE_LATE", channel = "InApp", language = "En",
            subject = "Branch A notice", body = "TENANT-A TEMPLATE: {{EmployeeName}} was late ({{CheckInTime}})."
        }, GatewayFactory.Json);
        created.EnsureSuccessStatusCode();

        await TestTenant.ReadAccepted(await a.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })));
        await TestTenant.ReadAccepted(await b.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })));
        await factory.DispatchDueAsync();

        var aBody = (await a.User("u1").GetFromJsonAsync<JsonElement>("/api/me/inbox")).GetProperty("items")[0].GetProperty("body").GetString();
        var bBody = (await b.User("u1").GetFromJsonAsync<JsonElement>("/api/me/inbox")).GetProperty("items")[0].GetProperty("body").GetString();
        aBody.Should().StartWith("TENANT-A TEMPLATE: Ahmed Ali");
        bBody.Should().NotContain("TENANT-A").And.Contain("checked in late");   // shared template
    }

    // ------------------------------------------------------------------------------------------------ T10

    [Fact(DisplayName = "T10 User disables a channel: that channel is not used")]
    public async Task T10_UserDisablesChannel()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableInAppAsync();
        await tenant.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "success" });

        (await tenant.User("emp-1").PutAsJsonAsync("/api/me/preferences", new { disabledChannels = new[] { "Sms" } }, GatewayFactory.Json))
            .EnsureSuccessStatusCode();

        var (id, deliveries) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp", "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();

        var n = await tenant.GetNotificationAsync(id);
        TestTenant.StatusOf(n, "Sms").Should().Be("Suppressed");
        TestTenant.DeliveryOf(n, "Sms").GetProperty("attempts").GetArrayLength().Should().Be(0);
        SentBy(deliveries["Sms"]).Should().BeNull();   // the SMS provider was never called
        TestTenant.StatusOf(n, "InApp").Should().Be("Delivered");
    }

    // ------------------------------------------------------------------------------------------------ T11

    [Fact(DisplayName = "T11 Duplicate notification request: duplicate processing is prevented")]
    public async Task T11_Idempotency()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableInAppAsync();
        var body = TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" });

        // Sequential replay → same notification, flagged as a replay.
        var key = Guid.NewGuid().ToString();
        var first = await TestTenant.ReadAccepted(await tenant.SendAsync(body, key));
        var replay = await tenant.SendAsync(body, key);
        replay.Headers.Contains("Idempotent-Replayed").Should().BeTrue();
        (await TestTenant.ReadAccepted(replay)).NotificationId.Should().Be(first.NotificationId);

        // Same key, different payload → 409 (never silently ignored).
        (await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u2" }), key)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Concurrent duplicates → exactly one notification row (unique index is the arbiter).
        var concurrentKey = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => tenant.SendAsync(body, concurrentKey)));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted || r.StatusCode == HttpStatusCode.Conflict);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginScope(tenant.Id);
        (await db.Notifications.CountAsync(n => n.IdempotencyKey == key)).Should().Be(1);
        (await db.Notifications.CountAsync(n => n.IdempotencyKey == concurrentKey)).Should().Be(1);

        // And the delivery itself is processed once: the user gets exactly one inbox item.
        await factory.DispatchDueAsync();
        (await tenant.User("u1").GetFromJsonAsync<JsonElement>("/api/me/inbox")).GetProperty("total").GetInt32().Should().Be(2);   // key + concurrentKey
    }

    // ------------------------------------------------------------------------------------------------ T12

    [Fact(DisplayName = "T12 Application restarts with pending messages: processing resumes safely")]
    public async Task T12_RestartResumes()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableInAppAsync();
        var smsConfig = (await tenant.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "success" }))[0];

        // (a) Queued but never dispatched before the "restart".
        var (pending, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })));

        // (b) A worker leased an SMS, committed "call in flight", then the process died.
        var (inFlight, inFlightDeliveries) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        var smsDelivery = inFlightDeliveries["Sms"];
        using (var scope = factory.Services.CreateScope())
        {
            var leased = await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>().LeaseDueAsync("dying-worker", 500, TimeSpan.FromMilliseconds(1), default);
            leased.Should().Contain(smsDelivery);
            await Task.Delay(50);   // let the 1 ms lease expire
            var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
            using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginScope(tenant.Id);
            var delivery = await db.Deliveries.FirstAsync(d => d.Id == smsDelivery);
            delivery.MarkCallInFlight(smsConfig, FakeSms, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();

            // Everything else that dying-worker grabbed goes back to the queue, as recovery would do.
            foreach (var other in leased.Where(x => x != smsDelivery))
                await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().RecoverExpiredLeaseAsync(other, default);
        }

        // "Restart": a brand-new host process on the same database.
        await using var restarted = factory.WithWebHostBuilder(_ => { });
        await Task.Delay(50);
        using (var scope = restarted.Services.CreateScope())
        {
            foreach (var expired in await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>().FindExpiredLeasesAsync(500, default))
                await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().RecoverExpiredLeaseAsync(expired, default);
        }
        await GatewayFactory.DispatchDueAsync(restarted.Services, "worker-after-restart");

        TestTenant.StatusOf(await tenant.GetNotificationAsync(pending), "InApp").Should().Be("Delivered");
        // The provider may already have sent the SMS: it is NOT re-sent blindly (no duplicate), it is marked Uncertain.
        TestTenant.StatusOf(await tenant.GetNotificationAsync(inFlight), "Sms").Should().Be("Uncertain");
        SentBy(smsDelivery).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------ T13

    [Fact(DisplayName = "T13 Provider timeout: timeout is enforced and handled without duplicate sends")]
    public async Task T13_Timeout()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableChannelAsync("Sms", maxAttempts: 3, timeoutSeconds: 1);
        var hang = await tenant.AddProviderAsync(FakeSms, new { mode = "hang" }, name: "hangs");
        var backup = await tenant.AddProviderAsync(FakeSms, new { mode = "success" }, name: "backup");
        await tenant.SetChainAsync("Sms", hang, backup);

        var (id, deliveries) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        var started = DateTime.UtcNow;
        await factory.DispatchDueAsync();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));   // the call was cut off at ~1 s

        var delivery = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Sms");
        delivery.GetProperty("status").GetString().Should().Be("Uncertain");
        var attempt = delivery.GetProperty("attempts")[0];
        attempt.GetProperty("errorCode").GetString().Should().Be("timeout");
        delivery.GetProperty("attempts").GetArrayLength().Should().Be(1);   // NO failover to the backup after a timeout
        SentBy(deliveries["Sms"]).Should().BeNull();
    }

    [Fact(DisplayName = "T13b Timeout before the request leaves (connect failure) is retried")]
    public async Task T13b_TransientIsRetried()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.UseFakeChainAsync(NotificationChannel.Sms, 5, new { failFirst = 1 });

        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();
        var first = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Sms");
        first.GetProperty("status").GetString().Should().Be("Retrying");
        first.GetProperty("nextAttemptAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);

        (await factory.DispatchUntilAsync(tenant, id, "Sms", TimeSpan.FromSeconds(10), "Sent")).Should().Be("Sent");
    }

    // ------------------------------------------------------------------------------------------------ T14

    [Fact(DisplayName = "T14 Invalid webhook signature: callback is rejected")]
    public async Task T14_InvalidWebhookSignature()
    {
        var tenant = await factory.CreateTenantAsync();
        var config = (await tenant.UseFakeChainAsync(NotificationChannel.Sms, 5, new { mode = "success" }))[0];
        var hook = await (await tenant.Admin.PostAsJsonAsync($"{tenant.Base}/webhooks", new { providerConfigurationId = config }))
            .Content.ReadFromJsonAsync<JsonElement>(GatewayFactory.Json);
        var path = hook.GetProperty("callbackPath").GetString()!;
        var secret = hook.GetProperty("signingSecret").GetString()!;

        var (id, _) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();
        var messageId = TestTenant.DeliveryOf(await tenant.GetNotificationAsync(id), "Sms").GetProperty("providerMessageId").GetString()!;
        var payload = JsonSerializer.Serialize(new { events = new[] { new { eventId = "evt-" + Guid.NewGuid(), providerMessageId = messageId, status = "delivered" } } });

        HttpRequestMessage Callback(string signature, long timestamp)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            r.Headers.Add("X-Fake-Signature", signature);
            r.Headers.Add("X-Fake-Timestamp", timestamp.ToString());
            return r;
        }

        var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stale = now - 3600;

        (await client.SendAsync(Callback("00ff", now))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);                                  // wrong signature
        (await client.SendAsync(Callback(FakeWebhookHandler.Sign("wrong-secret", payload, now), now))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Callback(FakeWebhookHandler.Sign(secret, payload, stale), stale))).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // replayed old callback
        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "Sms").Should().Be("Sent");   // nothing was applied

        var ok = await client.SendAsync(Callback(FakeWebhookHandler.Sign(secret, payload, now), now));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetInt32().Should().Be(1);
        TestTenant.StatusOf(await tenant.GetNotificationAsync(id), "Sms").Should().Be("Delivered");

        // Same event again (valid signature) → recorded as duplicate, not applied twice.
        var again = await client.SendAsync(Callback(FakeWebhookHandler.Sign(secret, payload, now), now));
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicates").GetInt32().Should().Be(1);
    }

    // ------------------------------------------------------------------------------------------------ T15

    [Fact(DisplayName = "T15 Quota exceeded: rejected (Reject policy) or deferred (Defer policy)")]
    public async Task T15_Quota()
    {
        // Reject policy (default) → 429 for the request that would exceed the quota.
        var rejecting = await factory.CreateTenantAsync();
        await rejecting.EnableInAppAsync();
        (await rejecting.PlatformAdmin().PutAsJsonAsync($"{rejecting.Base}/quotas", new { channel = (string?)null, period = "Daily", limit = 1 }, GatewayFactory.Json))
            .EnsureSuccessStatusCode();

        (await rejecting.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" }))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var over = await rejecting.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u2" }));
        over.StatusCode.Should().Be((HttpStatusCode)429);

        var quotas = await rejecting.Admin.GetFromJsonAsync<JsonElement>($"{rejecting.Base}/quotas", GatewayFactory.Json);
        quotas[0].GetProperty("usedInCurrentPeriod").GetInt64().Should().Be(1);   // the rejected request consumed nothing

        // Defer policy → accepted, but deliveries wait for the next period.
        var deferring = await factory.CreateTenantAsync();
        await deferring.EnableInAppAsync();
        var admin = factory.SystemAdmin();
        (await admin.PutAsJsonAsync($"/api/admin/tenants/{deferring.Id}/policies", new
        {
            criticalOverridesUserPreferences = true, quotaExceededPolicy = "Defer", requestsPerMinute = 600, defaultLanguage = "En", timeZoneId = "Africa/Cairo"
        }, GatewayFactory.Json)).EnsureSuccessStatusCode();
        (await deferring.PlatformAdmin().PutAsJsonAsync($"{deferring.Base}/quotas", new { channel = "InApp", period = "Daily", limit = 1 }, GatewayFactory.Json))
            .EnsureSuccessStatusCode();

        await TestTenant.ReadAccepted(await deferring.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u1" })));
        var deferred = await deferring.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, new { userId = "u2" }));
        deferred.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await deferred.Content.ReadFromJsonAsync<JsonElement>(GatewayFactory.Json);
        json.GetProperty("warnings").EnumerateArray().Should().Contain(w => w.GetString()!.Contains("quota"));
        json.GetProperty("deliveries")[0].GetProperty("nextAttemptAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(1));   // next period
    }

    // ------------------------------------------------------------------------------------------------ T16

    [Fact(DisplayName = "T16 Concurrent dispatchers: a delivery is never processed concurrently")]
    public async Task T16_ConcurrentDispatchers()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableInAppAsync();
        var recipients = Enumerable.Range(0, 60).Select(i => (object)new { userId = $"user-{i}" }).ToArray();
        await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp" }, recipients)));

        async Task<(string Worker, IReadOnlyList<Guid> Ids)> Lease(string worker)
        {
            using var scope = factory.Services.CreateScope();
            return (worker, await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>().LeaseDueAsync(worker, 25, TimeSpan.FromMinutes(1), default));
        }

        var concurrent = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => Lease($"w{i}")));

        // READPAST makes concurrent dispatchers SKIP rows another one is holding (they never wait and never double-lease),
        // so a concurrent round may lease fewer rows than are due. The rest are picked up by the next poll.
        var results = concurrent.ToList();
        while (true)
        {
            var next = await Lease("w5");
            if (next.Ids.Count == 0) break;
            results.Add(next);
        }

        var all = results.SelectMany(r => r.Ids).ToList();
        all.Should().OnlyHaveUniqueItems();               // no delivery leased by two dispatchers
        all.Count.Should().BeGreaterThanOrEqualTo(60);    // and none is lost

        // A worker that does not own the lease must not process the row.
        var stolen = results.First(r => r.Ids.Count > 0).Ids[0];
        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(stolen, "intruder", default)).Should().Be(ProcessResult.Skipped);

        // Owners process in parallel; every user gets exactly one inbox item.
        await Task.WhenAll(results.SelectMany(r => r.Ids.Select(async id =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(id, r.Worker, default);
        })));
        await factory.DispatchDueAsync();

        for (var i = 0; i < 60; i += 15)
            (await tenant.User($"user-{i}").GetFromJsonAsync<JsonElement>("/api/me/inbox")).GetProperty("total").GetInt32().Should().Be(1);
    }

    // ------------------------------------------------------------------------------------------------ T17

    [Fact(DisplayName = "T17 Arabic and English templates: correct localized content is generated")]
    public async Task T17_Localization()
    {
        var tenant = await factory.CreateTenantAsync(defaultLanguage: "En");
        await tenant.EnableInAppAsync();
        (await tenant.User("u-pref-ar").PutAsJsonAsync("/api/me/preferences", new { language = "Ar" }, GatewayFactory.Json)).EnsureSuccessStatusCode();

        await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "InApp" },
            new { userId = "u-ar", language = "Ar" },     // language in the request
            new { userId = "u-pref-ar" },                  // language from the user's preference
            new { userId = "u-en" })));                    // tenant default (En)
        await factory.DispatchDueAsync();

        async Task<string> Body(string user) =>
            (await tenant.User(user).GetFromJsonAsync<JsonElement>("/api/me/inbox")).GetProperty("items")[0].GetProperty("body").GetString()!;

        (await Body("u-ar")).Should().Contain("قام").And.Contain("Ahmed Ali");
        (await Body("u-pref-ar")).Should().Contain("متأخراً");
        (await Body("u-en")).Should().Contain("checked in late");

        var preview = await tenant.Admin.PostAsJsonAsync($"{tenant.Base}/templates/preview", new
        {
            code = "ATTENDANCE_LATE", channel = "Email", language = "Ar",
            data = new { EmployeeName = "منى", CheckInTime = "08:45", Date = "2026-11-22" }
        }, GatewayFactory.Json);
        var rendered = await preview.Content.ReadFromJsonAsync<JsonElement>();
        rendered.GetProperty("subject").GetString().Should().Be("تأخير في الحضور: منى");
        rendered.GetProperty("body").GetString().Should().Contain("dir=\"rtl\"");
    }

    // ------------------------------------------------------------------------------------------------ T18

    [Fact(DisplayName = "T18 Provider secret rotation: new credentials work without exposing secrets")]
    public async Task T18_SecretRotation()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.EnableChannelAsync("Sms");
        var config = await tenant.AddProviderAsync(FakeSms, new { mode = "success" }, new Dictionary<string, string> { ["apiKey"] = "OLD-SECRET-111" });
        await tenant.SetChainAsync("Sms", config);

        var (_, before) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();
        SentBy(before["Sms"])!.Configuration.Secret("apiKey").Should().Be("OLD-SECRET-111");

        var rotated = await tenant.Admin.PutAsJsonAsync($"{tenant.Base}/providers/{config}/secrets",
            new { secrets = new Dictionary<string, string> { ["apiKey"] = "NEW-SECRET-222" } }, GatewayFactory.Json);
        var rotatedBody = await rotated.Content.ReadAsStringAsync();
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        rotatedBody.Should().NotContain("NEW-SECRET-222").And.NotContain("OLD-SECRET-111");
        JsonDocument.Parse(rotatedBody).RootElement.GetProperty("secretVersion").GetInt32().Should().Be(2);

        var (_, after) = await TestTenant.ReadAccepted(await tenant.SendAsync(TestTenant.LateArrival(new[] { "Sms" }, SmsRecipient)));
        await factory.DispatchDueAsync();
        SentBy(after["Sms"])!.Configuration.Secret("apiKey").Should().Be("NEW-SECRET-222");

        // Never readable through the API, never stored in plain text.
        (await tenant.Admin.GetStringAsync($"{tenant.Base}/providers/{config}")).Should().NotContain("SECRET");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginScope(tenant.Id);
        var stored = await db.ProviderConfigurations.AsNoTracking().FirstAsync(c => c.Id == config);
        stored.EncryptedSecrets.Should().NotContain("NEW-SECRET-222");
        stored.ToString().Should().NotContain("NEW-SECRET-222");
    }
}
