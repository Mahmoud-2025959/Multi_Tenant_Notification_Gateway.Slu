using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Settings;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>Platform operator: tenant onboarding + policies.</summary>
[ApiController]
[Route("api/admin/tenants")]
[Authorize(Policy = Policies.SystemAdmin)]
public sealed class AdminTenantsController(TenantAdminService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<TenantDto>> List(CancellationToken ct) => service.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<TenantDto> Get(Guid id, CancellationToken ct) => service.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = await service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = tenant.Id }, tenant);
    }

    [HttpPut("{id:guid}/policies")]
    public Task<TenantDto> UpdatePolicies(Guid id, UpdateTenantPoliciesRequest request, CancellationToken ct) => service.UpdatePoliciesAsync(id, request, ct);

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) { await service.SetActiveAsync(id, true, ct); return NoContent(); }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) { await service.SetActiveAsync(id, false, ct); return NoContent(); }
}

/// <summary>Provider catalog (what the gateway CAN use). Read for tenant admins, write for the platform.</summary>
[ApiController]
[Route("api/providers")]
public sealed class ProviderCatalogController(ProviderConfigurationService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.TenantAdmin)]
    public Task<IReadOnlyList<ProviderCatalogDto>> List(CancellationToken ct) => service.CatalogAsync(ct);

    [HttpPut("{key}")]
    [Authorize(Policy = Policies.SystemAdmin)]
    public async Task<IActionResult> Update(string key, UpdateCatalogEntryRequest request, CancellationToken ct)
    {
        await service.UpdateCatalogEntryAsync(key, request, ct);
        return NoContent();
    }

    /// <summary>After adding a new master key and switching ActiveKeyId: re-encrypt all envelopes with it.</summary>
    [HttpPost("re-encrypt-secrets")]
    [Authorize(Policy = Policies.SystemAdmin)]
    public async Task<IActionResult> ReEncrypt(CancellationToken ct) => Ok(new { reEncrypted = await service.ReEncryptAllAsync(ct) });
}
