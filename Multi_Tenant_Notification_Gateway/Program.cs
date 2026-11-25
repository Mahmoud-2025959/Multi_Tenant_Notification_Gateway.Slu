using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Multi_Tenant_Notification_Gateway.Auth;
using Multi_Tenant_Notification_Gateway.Middleware;
using Multi_Tenant_Notification_Gateway.Realtime;
using Multi_Tenant_Notification_Gateway.Seed;
using NotificationGateway.Application;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Infrastructure;
using NotificationGateway.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext());

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddGatewayAuth(builder.Configuration);
builder.Services.AddGatewayRateLimiting();
builder.Services.AddMemoryCache();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Multi-Tenant Notification Gateway", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT from the Attendance System (or POST /api/dev/token in Development)."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
    c.CustomSchemaIds(t => t.FullName?.Replace("+", "."));
});

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<NotificationGatewayDbContext>().Database.MigrateAsync();
}
if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Database:SeedDemoData", true))
{
    await DevDataSeeder.SeedAsync(app.Services);
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Practical demonstration only: serves wwwroot/demo/live.html (same origin as the SignalR hub, so no CORS needed).
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHealthChecks("/health");

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program { }
