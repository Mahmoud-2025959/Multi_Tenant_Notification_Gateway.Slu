using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Multi_Tenant_Notification_Gateway.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Must match the issuer of the Attendance System's tokens.</summary>
    public string Issuer { get; set; } = "attendance-system";
    public string Audience { get; set; } = "notification-gateway";

    /// <summary>Symmetric key shared with the token issuer (≥ 32 chars). In production comes from a secret store.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Exposes POST /api/dev/token. Never enable outside Development.</summary>
    public bool EnableDevTokens { get; set; }
}

/// <summary>Claim + role names agreed with the token issuer.</summary>
public static class GatewayClaims
{
    public const string TenantId = "tenant_id";
    public const string Subject = "sub";
    public const string Role = "role";
}

public static class Roles
{
    /// <summary>Platform operator: tenants, shared providers/templates, provider catalog.</summary>
    public const string SystemAdmin = "SystemAdmin";
    /// <summary>Tenant administrator: the tenant's channels, providers, templates, routing, quotas, reports.</summary>
    public const string TenantAdmin = "TenantAdmin";
    /// <summary>A business module (e.g. the Attendance System backend) that sends notifications.</summary>
    public const string Service = "Service";
    /// <summary>End user: own inbox and preferences.</summary>
    public const string TenantUser = "TenantUser";
}

public static class Policies
{
    public const string SystemAdmin = "SystemAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string Sender = "Sender";
    public const string TenantMember = "TenantMember";
}

public static class AuthSetup
{
    public static IServiceCollection AddGatewayAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (jwt.SigningKey.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be configured (at least 32 characters).");
        services.AddSingleton(jwt);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;   // keep "sub", "role", "tenant_id" exactly as issued
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = GatewayClaims.Subject,
                    RoleClaimType = GatewayClaims.Role
                };
                o.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on WebSockets: SignalR sends the token as ?access_token=.
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.SystemAdmin, p => p.RequireRole(Roles.SystemAdmin));
            o.AddPolicy(Policies.TenantAdmin, p => p.RequireRole(Roles.TenantAdmin, Roles.SystemAdmin));
            o.AddPolicy(Policies.Sender, p => p.RequireRole(Roles.Service, Roles.TenantAdmin, Roles.SystemAdmin));
            o.AddPolicy(Policies.TenantMember, p => p.RequireAuthenticatedUser());
        });

        return services;
    }

    /// <summary>Creates a token in the same shape the Attendance System issues (dev endpoint + tests).</summary>
    public static string CreateToken(JwtOptions jwt, string userId, Guid? tenantId, IEnumerable<string> roles, TimeSpan lifetime)
    {
        var claims = new List<Claim> { new(GatewayClaims.Subject, userId), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) };
        if (tenantId is not null) claims.Add(new Claim(GatewayClaims.TenantId, tenantId.Value.ToString()));
        claims.AddRange(roles.Select(r => new Claim(GatewayClaims.Role, r)));

        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, DateTime.UtcNow, DateTime.UtcNow.Add(lifetime), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
