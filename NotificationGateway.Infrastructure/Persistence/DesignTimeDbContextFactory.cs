using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NotificationGateway.Infrastructure.Tenancy;

namespace NotificationGateway.Infrastructure.Persistence;

/// <summary>
/// Used only by "dotnet ef migrations add". The tenant context is irrelevant at design time.
///   dotnet ef migrations add Name -p NotificationGateway.Infrastructure -s Multi_Tenant_Notification_Gateway -o Migrations
///   dotnet ef database update      -p NotificationGateway.Infrastructure -s Multi_Tenant_Notification_Gateway
/// Default target: LocalDB (same as appsettings.json). Override with the NG_DESIGN_CONNECTION environment variable.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotificationGatewayDbContext>
{
    public NotificationGatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NG_DESIGN_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=NotificationGateway;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<NotificationGatewayDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(NotificationGatewayDbContext).Assembly.FullName))
            .Options;

        return new NotificationGatewayDbContext(options, new TenantContext(), TimeProvider.System);
    }
}
