using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationGateway.Application.Abstractions;

namespace NotificationGateway.Infrastructure.Persistence;

internal sealed class EfUnitOfWork(NotificationGatewayDbContext db) : IUnitOfWork
{
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        // Already inside a transaction (nested call): just run.
        if (db.Database.CurrentTransaction is not null) return work(cancellationToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async ct =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await work(ct);
            await tx.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}

/// <summary>
/// Leasing with UPDLOCK + READPAST: each dispatcher instance atomically claims rows nobody else holds, skipping locked
/// rows instead of waiting. Highest priority first, then oldest due. Works across any number of API/worker instances.
/// </summary>
internal sealed class SqlDeliveryLeaseStore(NotificationGatewayDbContext db, TimeProvider clock) : IDeliveryLeaseStore
{
    private const string LeaseSql = """
        WITH due AS (
            SELECT TOP (@batch) *
            FROM [NotificationDeliveries] WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE [Status] IN (1, 6) AND [NextAttemptAt] <= @now
            ORDER BY [Priority] DESC, [NextAttemptAt]
        )
        UPDATE due
        SET [Status] = 2, [LockedBy] = @worker, [LockedUntil] = @until, [UpdatedAt] = @now
        OUTPUT inserted.[Id];
        """;

    private const string ExpiredSql = """
        SELECT TOP (@batch) [Id]
        FROM [NotificationDeliveries] WITH (READPAST)
        WHERE [Status] = 2 AND [LockedUntil] < @now
        ORDER BY [LockedUntil];
        """;

    public async Task<IReadOnlyList<Guid>> LeaseDueAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await QueryIdsAsync(LeaseSql, ct,
            ("@batch", batchSize), ("@now", now), ("@worker", workerId), ("@until", now.Add(lease)));
    }

    public async Task<IReadOnlyList<Guid>> FindExpiredLeasesAsync(int batchSize, CancellationToken ct) =>
        await QueryIdsAsync(ExpiredSql, ct, ("@batch", batchSize), ("@now", clock.GetUtcNow()));

    private async Task<List<Guid>> QueryIdsAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                command.Parameters.Add(p);
            }

            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            return ids;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}

/// <summary>
/// Concurrency-safe quota counter. The conditional UPDATE is atomic in SQL Server, so two API instances racing for the
/// last unit cannot both succeed. Runs in the caller's transaction (rolled back with the request).
/// </summary>
internal sealed class SqlQuotaCounter(NotificationGatewayDbContext db) : IQuotaCounter
{
    private const string Sql = """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM [NotificationQuotaUsages] WITH (UPDLOCK, HOLDLOCK)
                       WHERE [TenantId] = @tenant AND [ChannelKey] = @channel AND [PeriodKey] = @period)
            INSERT INTO [NotificationQuotaUsages] ([TenantId], [ChannelKey], [PeriodKey], [Used]) VALUES (@tenant, @channel, @period, 0);
        SET NOCOUNT OFF;
        UPDATE [NotificationQuotaUsages]
        SET [Used] = [Used] + @amount
        WHERE [TenantId] = @tenant AND [ChannelKey] = @channel AND [PeriodKey] = @period AND [Used] + @amount <= @limit;
        """;

    public async Task<bool> TryConsumeAsync(Guid tenantId, string channelKey, string periodKey, long amount, long limit, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(Sql, new object[]
        {
            new SqlParameter("@tenant", tenantId),
            new SqlParameter("@channel", channelKey),
            new SqlParameter("@period", periodKey),
            new SqlParameter("@amount", amount),
            new SqlParameter("@limit", limit)
        }, ct);
        return affected == 1;
    }
}

/// <summary>Insert-once with the unique index (ProviderKey, EventId) as the arbiter (also safe across instances).</summary>
internal sealed class SqlWebhookEventStore(NotificationGatewayDbContext db) : IWebhookEventStore
{
    private const string Sql = """
        INSERT INTO [ProcessedWebhookEvents] ([ProviderKey], [EventId], [TenantId], [ReceivedAt])
        SELECT @provider, @event, @tenant, @at
        WHERE NOT EXISTS (SELECT 1 FROM [ProcessedWebhookEvents] WHERE [ProviderKey] = @provider AND [EventId] = @event);
        """;

    public async Task<bool> TryRecordAsync(string providerKey, string eventId, Guid tenantId, DateTimeOffset receivedAt, CancellationToken ct)
    {
        try
        {
            var rows = await db.Database.ExecuteSqlRawAsync(Sql, new object[]
            {
                new SqlParameter("@provider", providerKey),
                new SqlParameter("@event", eventId.Length > 256 ? eventId[..256] : eventId),
                new SqlParameter("@tenant", tenantId),
                new SqlParameter("@at", receivedAt)
            }, ct);
            return rows == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;   // lost a race with an identical concurrent callback
        }
    }
}
