using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multi_Tenant_Notification_Gateway.Auth;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>Development only: issues tokens shaped like the Attendance System's, for Swagger and manual testing.</summary>
[ApiController]
[Route("api/dev/token")]
[AllowAnonymous]
[ApiExplorerSettings(GroupName = null)]
public sealed class DevTokenController(JwtOptions jwt) : ControllerBase
{
    public sealed record DevTokenRequest(string UserId, Guid? TenantId, string[] Roles, int LifetimeMinutes = 120);

    [HttpPost]
    public IActionResult Create(DevTokenRequest request)
    {
        if (!jwt.EnableDevTokens) return NotFound();
        var token = AuthSetup.CreateToken(jwt, request.UserId, request.TenantId, request.Roles ?? Array.Empty<string>(),
            TimeSpan.FromMinutes(Math.Clamp(request.LifetimeMinutes, 1, 24 * 60)));
        return Ok(new { accessToken = token, tokenType = "Bearer" });
    }
}
