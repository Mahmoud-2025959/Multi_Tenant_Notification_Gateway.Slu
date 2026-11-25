using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Tenants;

namespace NotificationGateway.Application.Settings;

public sealed record TenantDto(
    Guid Id, string Code, string Name, bool IsActive, TemplateLanguage DefaultLanguage, string TimeZoneId,
    bool CriticalOverridesUserPreferences, QuotaExceededPolicy QuotaExceededPolicy, int RequestsPerMinute, DateTimeOffset CreatedAt);

public sealed record CreateTenantRequest(string Code, string Name, TemplateLanguage DefaultLanguage = TemplateLanguage.Ar, string TimeZoneId = "Africa/Cairo");

public sealed record UpdateTenantPoliciesRequest(
    bool CriticalOverridesUserPreferences, QuotaExceededPolicy QuotaExceededPolicy, int RequestsPerMinute,
    TemplateLanguage DefaultLanguage, string TimeZoneId);

/// <summary>Platform operator only (enforced by the "SystemAdmin" policy). Onboarding a tenant is data only.</summary>
public sealed class TenantAdminService(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<TenantDto>> ListAsync(CancellationToken ct) =>
        (await db.Tenants.AsNoTracking().OrderBy(t => t.Code).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<TenantDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id));

    public async Task<TenantDto> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        Guard.Required(request.Code, nameof(request.Code));
        Guard.Required(request.Name, nameof(request.Name));
        Guard.MaxLength(request.Code, 64, nameof(request.Code));
        Guard.MaxLength(request.Name, 200, nameof(request.Name));
        Guard.TimeZone(request.TimeZoneId, nameof(request.TimeZoneId));

        var code = request.Code.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Code == code, ct))
            throw new ConflictException("tenant.code_taken", $"Tenant code '{code}' already exists.");

        var tenant = new Tenant(code, request.Name.Trim(), request.DefaultLanguage, request.TimeZoneId);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    public async Task<TenantDto> UpdatePoliciesAsync(Guid id, UpdateTenantPoliciesRequest request, CancellationToken ct)
    {
        Guard.TimeZone(request.TimeZoneId, nameof(request.TimeZoneId));
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        tenant.UpdatePolicies(request.CriticalOverridesUserPreferences, request.QuotaExceededPolicy, request.RequestsPerMinute,
            request.DefaultLanguage, request.TimeZoneId);
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        if (active) tenant.Activate(); else tenant.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    internal static TenantDto ToDto(Tenant t) => new(t.Id, t.Code, t.Name, t.IsActive, t.DefaultLanguage, t.TimeZoneId,
        t.CriticalOverridesUserPreferences, t.QuotaExceededPolicy, t.RequestsPerMinute, t.CreatedAt);
}
