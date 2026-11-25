using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Infrastructure.Persistence;
using NotificationGateway.Infrastructure.Providers;
using Testcontainers.MsSql;
using Xunit;

namespace NotificationGateway.IntegrationTests;

/// <summary>
/// The real API host on a real SQL Server.
///   * Default on Windows: a throw-away LocalDB database (no Docker needed), dropped after the run.
///   * NG_TEST_SQL=docker (or non-Windows): SQL Server in a Testcontainers container.
///   * NG_TEST_CONNECTION=...: any SQL Server you point it at.
/// Background workers and RabbitMQ are off so every test drives the dispatcher deterministically.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string _connectionString = string.Empty;
    private bool _dropDatabaseAtEnd;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Fake SendGrid API used by T04 (provider B is a REAL adapter talking to a stubbed HTTP endpoint).</summary>
    public static readonly StubHttpHandler SendGridStub = new(HttpStatusCode.Accepted, "", ("X-Message-Id", "sg-stub-message"));

    public async Task InitializeAsync()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("NG_TEST_CONNECTION");
        var useDocker = Environment.GetEnvironmentVariable("NG_TEST_SQL") == "docker" || !OperatingSystem.IsWindows();

        if (!string.IsNullOrWhiteSpace(explicitConnection))
        {
            _connectionString = explicitConnection;
        }
        else if (useDocker)
        {
            _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        else
        {
            _connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database=NG_Tests_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";
            _dropDatabaseAtEnd = true;
        }

        _ = Server;   // builds the host: migrates + seeds demo data
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_dropDatabaseAtEnd)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>().Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // UseSetting values are visible while Program.cs registers services (unlike late configuration sources).
        builder.UseSetting("ConnectionStrings:NotificationGateway", _connectionString);
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("RabbitMq:Enabled", "false");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:SeedDemoData", "true");

        builder.ConfigureTestServices(services =>
            services.AddHttpClient(SendGridProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => SendGridStub));
    }

    public JwtOptions Jwt => Services.GetRequiredService<JwtOptions>();

    public HttpClient ClientFor(Guid? tenantId, string userId, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenantId, userId, roles));
        return client;
    }

    public string Token(Guid? tenantId, string userId, params string[] roles) =>
        AuthSetup.CreateToken(Jwt, userId, tenantId, roles, TimeSpan.FromMinutes(30));

    public HttpClient SystemAdmin() => ClientFor(null, "platform-admin", Roles.SystemAdmin);

    /// <summary>Leases and processes everything due right now, through this host.</summary>
    public Task<List<ProcessResult>> DispatchDueAsync(string workerId = "test-worker") => DispatchDueAsync(Services, workerId);

    public static async Task<List<ProcessResult>> DispatchDueAsync(IServiceProvider services, string workerId = "test-worker")
    {
        var results = new List<ProcessResult>();
        for (var round = 0; round < 10; round++)
        {
            IReadOnlyList<Guid> ids;
            using (var scope = services.CreateScope())
                ids = await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>().LeaseDueAsync(workerId, 200, TimeSpan.FromMinutes(2), default);
            if (ids.Count == 0) break;

            foreach (var id in ids)
            {
                using var scope = services.CreateScope();
                results.Add(await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(id, workerId, default));
            }
        }
        return results;
    }

    /// <summary>Keeps dispatching (with the real backoff delays) until the channel's delivery reaches one of the statuses.</summary>
    public async Task<string> DispatchUntilAsync(TestTenant tenant, Guid notificationId, string channel, TimeSpan timeout, params string[] statuses)
    {
        var deadline = DateTime.UtcNow + timeout;
        string status;
        do
        {
            await DispatchDueAsync();
            status = TestTenant.StatusOf(await tenant.GetNotificationAsync(notificationId), channel);
            if (statuses.Contains(status)) return status;
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return status;
    }

    /// <summary>Creates an isolated tenant through the admin API so tests never interfere with each other.</summary>
    public async Task<TestTenant> CreateTenantAsync(string defaultLanguage = "En")
    {
        var code = "t-" + Guid.NewGuid().ToString("N")[..10];
        var response = await SystemAdmin().PostAsJsonAsync("/api/admin/tenants",
            new { code, name = code, defaultLanguage, timeZoneId = "Africa/Cairo" }, Json);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        return new TestTenant(this, id, code, ClientFor(id, "admin-" + code, Roles.TenantAdmin), ClientFor(id, "attendance-service", Roles.Service));
    }
}

public sealed class TestTenant(GatewayFactory factory, Guid id, string code, HttpClient admin, HttpClient sender)
{
    public Guid Id { get; } = id;
    public string Code { get; } = code;
    public HttpClient Admin { get; } = admin;
    public HttpClient Sender { get; } = sender;
    public string Base => $"/api/tenants/{Code}";

    public HttpClient User(string userId) => factory.ClientFor(Id, userId, Roles.TenantUser);
    public HttpClient PlatformAdmin() => factory.ClientFor(Id, "platform-admin", Roles.SystemAdmin);

    public async Task EnableChannelAsync(string channel, int maxAttempts = 5, int timeoutSeconds = 5)
    {
        var r = await Admin.PutAsJsonAsync($"{Base}/channels/{channel}",
            new { isEnabled = true, maxAttempts, baseRetryDelaySeconds = 1, maxRetryDelaySeconds = 2, providerTimeoutSeconds = timeoutSeconds }, GatewayFactory.Json);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
    }

    public async Task<Guid> AddProviderAsync(string providerKey, object settings, object? secrets = null, string name = "provider")
    {
        var r = await Admin.PostAsJsonAsync($"{Base}/providers",
            new { providerKey, name, settings, secrets = secrets ?? new Dictionary<string, string>(), senderIdentity = "TEST" }, GatewayFactory.Json);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>(GatewayFactory.Json)).GetProperty("id").GetGuid();
    }

    public async Task SetChainAsync(string channel, params Guid[] configurationIds)
    {
        var r = await Admin.PutAsJsonAsync($"{Base}/channels/{channel}/providers", new { providerConfigurationIds = configurationIds }, GatewayFactory.Json);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
    }

    /// <summary>Enables InApp/SignalR with the platform's shared configuration (seeded).</summary>
    public async Task EnableSharedAsync(string channel, string providerKey)
    {
        await EnableChannelAsync(channel);
        var list = await Admin.GetFromJsonAsync<JsonElement>($"{Base}/providers?channel={channel}", GatewayFactory.Json);
        var shared = list.EnumerateArray().First(c => c.GetProperty("providerKey").GetString() == providerKey && c.GetProperty("isShared").GetBoolean());
        await SetChainAsync(channel, shared.GetProperty("id").GetGuid());
    }

    public Task EnableInAppAsync() => EnableSharedAsync("InApp", InAppProvider.Key);

    /// <summary>One-call setup: channel enabled with a chain of fake providers configured with the given settings.</summary>
    public async Task<Guid[]> UseFakeChainAsync(NotificationChannel channel, int maxAttempts, params object[] settingsPerProvider)
    {
        await EnableChannelAsync(channel.ToString(), maxAttempts);
        var ids = new List<Guid>();
        for (var i = 0; i < settingsPerProvider.Length; i++)
            ids.Add(await AddProviderAsync(FakeProvider.KeyFor(channel), settingsPerProvider[i], name: $"fake-{i}"));
        await SetChainAsync(channel.ToString(), ids.ToArray());
        return ids.ToArray();
    }

    public Task<HttpResponseMessage> SendAsync(object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/send") { Content = JsonContent.Create(body, options: GatewayFactory.Json) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return Sender.SendAsync(request);
    }

    public static object LateArrival(string[] channels, params object[] recipients) => new
    {
        notificationType = "attendance.late",
        templateCode = "ATTENDANCE_LATE",
        priority = "Normal",
        data = new { EmployeeName = "Ahmed Ali", CheckInTime = "09:42", Date = "2026-11-22" },
        recipients,
        channels
    };

    public async Task<JsonElement> GetNotificationAsync(Guid notificationId) =>
        await Sender.GetFromJsonAsync<JsonElement>($"/api/notifications/{notificationId}", GatewayFactory.Json);

    public static async Task<(Guid NotificationId, Dictionary<string, Guid> DeliveryByChannel)> ReadAccepted(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(GatewayFactory.Json);
        var byChannel = new Dictionary<string, Guid>();
        foreach (var d in json.GetProperty("deliveries").EnumerateArray())
            byChannel.TryAdd(d.GetProperty("channel").GetString()!, d.GetProperty("deliveryId").GetGuid());
        return (json.GetProperty("notificationId").GetGuid(), byChannel);
    }

    public static JsonElement DeliveryOf(JsonElement notification, string channel) =>
        notification.GetProperty("deliveries").EnumerateArray().First(d => d.GetProperty("channel").GetString() == channel);

    public static string StatusOf(JsonElement notification, string channel) =>
        DeliveryOf(notification, channel).GetProperty("status").GetString()!;
}

/// <summary>Records requests and returns a canned response (stands in for a vendor API).</summary>
public sealed class StubHttpHandler(HttpStatusCode status, string body, params (string Name, string Value)[] headers) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, content));
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayFactory>
{
    public const string Name = "gateway";
}
