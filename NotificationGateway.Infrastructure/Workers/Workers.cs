using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Providers;
using NotificationGateway.Infrastructure.Messaging;
using NotificationGateway.Infrastructure.Persistence;

namespace NotificationGateway.Infrastructure.Workers;

public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    /// <summary>Turn off in API-only instances or in tests that drive the dispatcher manually.</summary>
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 20;
    public int Parallelism { get; set; } = 8;
    public int PollSeconds { get; set; } = 2;

    /// <summary>Must be longer than the slowest provider timeout + bookkeeping.</summary>
    public int LeaseSeconds { get; set; } = 120;
}

/// <summary>Dispatcher loop: woken by RabbitMQ / local signal, or by the poll interval as a safety net.</summary>
public sealed class DispatcherWorker(
    IServiceScopeFactory scopes,
    DispatchSignal signal,
    IOptions<WorkerOptions> options,
    ILogger<DispatcherWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Dispatcher {WorkerId} started", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Guid> leased;
                using (var scope = scopes.CreateScope())
                {
                    leased = await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>()
                        .LeaseDueAsync(_workerId, o.BatchSize, TimeSpan.FromSeconds(o.LeaseSeconds), stoppingToken);
                }

                if (leased.Count == 0)
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(leased, new ParallelOptions { MaxDegreeOfParallelism = o.Parallelism, CancellationToken = stoppingToken },
                    async (id, ct) =>
                    {
                        using var scope = scopes.CreateScope();
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(id, _workerId, ct);
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            logger.LogInformation("Delivery {DeliveryId} changed concurrently (e.g. cancelled); skipped.", id);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Lease expiry + recovery will pick it up again.
                            logger.LogError(ex, "Processing delivery {DeliveryId} failed", id);
                        }
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatcher loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

/// <summary>Finds deliveries whose worker died (lease expired) and recovers them safely (retry or Uncertain).</summary>
public sealed class LeaseRecoveryWorker(IServiceScopeFactory scopes, ILogger<LeaseRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Guid> expired;
                using (var scope = scopes.CreateScope())
                    expired = await scope.ServiceProvider.GetRequiredService<IDeliveryLeaseStore>().FindExpiredLeasesAsync(100, stoppingToken);

                foreach (var id in expired)
                {
                    using var scope = scopes.CreateScope();
                    try { await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().RecoverExpiredLeaseAsync(id, stoppingToken); }
                    catch (DbUpdateConcurrencyException) { /* another instance recovered it */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Lease recovery error"); }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

/// <summary>
/// Transactional outbox relay: publishes committed outbox rows to RabbitMQ. If the broker is down the row stays
/// pending and is retried; dispatchers still find the work by polling, so nothing is lost either way.
/// </summary>
public sealed class OutboxRelayWorker(
    IServiceScopeFactory scopes,
    IMessageBusPublisher bus,
    DispatchSignal signal,
    TimeProvider clock,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                var db = scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>();
                using var tenantScope = tenant.BeginSystemScope();

                var now = clock.GetUtcNow();
                var pending = await db.OutboxMessages
                    .Where(m => m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil <= now))
                    .OrderBy(m => m.OccurredAt)
                    .Take(100)
                    .ToListAsync(stoppingToken);

                foreach (var message in pending)
                {
                    if (bus.TryPublish(message.Type, message.Payload))
                    {
                        message.MarkProcessed(clock.GetUtcNow());
                        published++;
                    }
                    else
                    {
                        message.MarkFailed("Broker unavailable.", clock.GetUtcNow().AddSeconds(Math.Min(300, 5 * (message.Attempts + 1))));
                        signal.Notify();   // still wake the local dispatcher
                    }
                }
                if (pending.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Outbox relay error"); }

            if (published == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

/// <summary>Keeps the NotificationProviders catalog in sync with the adapters compiled into this build.</summary>
public sealed class ProviderCatalogSync(IServiceScopeFactory scopes, ILogger<ProviderCatalogSync> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await SyncAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Provider catalog sync failed (database not ready?)");
        }
    }

    public static async Task SyncAsync(IServiceProvider services, CancellationToken ct)
    {
        var registry = services.GetRequiredService<IProviderRegistry>();
        var tenant = services.GetRequiredService<ITenantContext>();
        var db = services.GetRequiredService<NotificationGatewayDbContext>();
        using var tenantScope = tenant.BeginSystemScope();

        var existing = await db.NotificationProviders.ToDictionaryAsync(p => p.Key, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var adapter in registry.All)
        {
            if (existing.TryGetValue(adapter.ProviderName, out var row))
                row.Sync(adapter.Channel, adapter.Descriptor.DisplayName, adapter.Descriptor.Capabilities);
            else
                db.NotificationProviders.Add(new NotificationProvider(adapter.ProviderName, adapter.Channel, adapter.Descriptor.DisplayName, adapter.Descriptor.Capabilities));
        }
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
