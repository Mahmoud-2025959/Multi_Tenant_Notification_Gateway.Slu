using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multi_Tenant_Notification_Gateway.Auth;
using NotificationGateway.Application.Templates;

namespace Multi_Tenant_Notification_Gateway.Controllers;

/// <summary>
/// Templates (spec §5.E): tenant-owned or shared (SystemAdmin), per channel, Arabic/English, versioned, active/inactive.
/// "Editing" creates version N+1; older versions are deactivated, never changed, so history stays reproducible.
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId}/templates")]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class NotificationTemplatesController(TemplateService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<TemplateDto>> List([FromQuery] string? code, [FromQuery] bool includeInactive, CancellationToken ct) =>
        service.ListAsync(code, includeInactive, ct);

    [HttpPost]
    public Task<TemplateDto> CreateVersion(CreateTemplateVersionRequest request, CancellationToken ct) => service.CreateVersionAsync(request, ct);

    [HttpPost("{templateId:guid}/activate")]
    public async Task<IActionResult> Activate(Guid templateId, CancellationToken ct) { await service.SetActiveAsync(templateId, true, ct); return NoContent(); }

    [HttpPost("{templateId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid templateId, CancellationToken ct) { await service.SetActiveAsync(templateId, false, ct); return NoContent(); }

    /// <summary>Renders with sample data using the same resolution rules as real sends; reports missing placeholders.</summary>
    [HttpPost("preview")]
    public Task<RenderedContent> Preview(PreviewTemplateRequest request, CancellationToken ct) => service.PreviewAsync(request, ct);
}
