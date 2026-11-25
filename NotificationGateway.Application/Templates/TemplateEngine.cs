using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Templates;

namespace NotificationGateway.Application.Templates;

public sealed record RenderedContent(string? Subject, string Body);

/// <summary>
/// "{{Name}}" placeholder rendering. Deliberately NOT a scripting engine: no expressions, no loops, no code execution,
/// so a tenant-authored template can never run code on the gateway.
/// Values are HTML-encoded for e-mail bodies (prevents HTML injection through caller data); other channels get raw text.
/// </summary>
public static class TemplateRenderer
{
    public static RenderedContent Render(NotificationTemplate template, JsonElement data, bool htmlEncodeValues)
    {
        var values = ToDictionary(data);
        var missing = template.PlaceholderNames.Where(p => !values.ContainsKey(p)).ToList();
        if (missing.Count > 0)
            throw RequestValidationException.For("Data",
                $"Template {template.Code} ({template.Channel}/{template.Language}) is missing values for: {string.Join(", ", missing)}.");

        string Replace(string text, bool encode) => NotificationTemplate.PlaceholderRegex().Replace(text, m =>
        {
            var value = values[m.Groups["name"].Value];
            return encode ? WebUtility.HtmlEncode(value) : value;
        });

        return new RenderedContent(
            template.Subject is null ? null : Replace(template.Subject, false),
            Replace(template.Body, htmlEncodeValues));
    }

    public static Dictionary<string, string> ToDictionary(JsonElement data)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind != JsonValueKind.Object) return dict;
        foreach (var p in data.EnumerateObject())
        {
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => p.Value.GetRawText()
            };
        }
        return dict;
    }
}

/// <summary>
/// Resolution order: tenant template (requested language) → tenant template (tenant default language)
/// → shared template (requested language) → shared template (tenant default language). Latest active version wins.
/// </summary>
public sealed class TemplateResolver(IApplicationDbContext db)
{
    public async Task<NotificationTemplate?> ResolveAsync(Guid tenantId, string code, NotificationChannel channel,
        TemplateLanguage requested, TemplateLanguage tenantDefault, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var candidates = await db.Templates.AsNoTracking()
            .Where(t => t.Code == normalized && t.Channel == channel && t.IsActive)
            .ToListAsync(ct);

        NotificationTemplate? Pick(Guid? owner, TemplateLanguage language) => candidates
            .Where(t => t.TenantId == owner && t.Language == language)
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();

        return Pick(tenantId, requested) ?? Pick(tenantId, tenantDefault) ?? Pick(null, requested) ?? Pick(null, tenantDefault);
    }
}

public sealed record TemplateDto(
    Guid Id, bool IsShared, string Code, NotificationChannel Channel, TemplateLanguage Language, int Version,
    string? Subject, string Body, IReadOnlyCollection<string> Placeholders, bool IsActive, DateTimeOffset CreatedAt, string? CreatedBy);

public sealed record CreateTemplateVersionRequest(
    string Code, NotificationChannel Channel, TemplateLanguage Language, string? Subject, string Body, bool Shared = false);

public sealed record PreviewTemplateRequest(string Code, NotificationChannel Channel, TemplateLanguage Language, JsonElement Data);

/// <summary>Templates are versioned: "editing" creates version N+1 and deactivates older versions of the same key.</summary>
public sealed class TemplateService(IApplicationDbContext db, ITenantContext tenant, TemplateResolver resolver, TimeProvider clock)
{
    public async Task<IReadOnlyList<TemplateDto>> ListAsync(string? code, bool includeInactive, CancellationToken ct)
    {
        var query = db.Templates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(code)) query = query.Where(t => t.Code == code.Trim().ToUpper());
        if (!includeInactive) query = query.Where(t => t.IsActive);
        var rows = await query.OrderBy(t => t.Code).ThenBy(t => t.Channel).ThenBy(t => t.Language).ThenByDescending(t => t.Version).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TemplateDto> CreateVersionAsync(CreateTemplateVersionRequest request, CancellationToken ct)
    {
        Guard.Required(request.Code, nameof(request.Code));
        Guard.Required(request.Body, nameof(request.Body));
        Guard.MaxLength(request.Code, 128, nameof(request.Code));
        Guard.MaxLength(request.Subject, 512, nameof(request.Subject));
        if (request.Shared && !tenant.IsSystemAdmin)
            throw new TenantAccessDeniedException("Only system administrators can create shared templates.");

        Guid? owner = request.Shared ? null : tenant.RequireTenantId();
        var code = request.Code.Trim().ToUpperInvariant();

        var previous = await db.Templates
            .Where(t => t.TenantId == owner && t.Code == code && t.Channel == request.Channel && t.Language == request.Language)
            .ToListAsync(ct);

        var version = previous.Count == 0 ? 1 : previous.Max(t => t.Version) + 1;
        foreach (var old in previous.Where(t => t.IsActive)) old.Deactivate();

        var template = new NotificationTemplate(owner, code, request.Channel, request.Language, version, request.Subject, request.Body,
            clock.GetUtcNow(), tenant.UserId);
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        return ToDto(template);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Template", id);
        if (active) template.Activate(); else template.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    public async Task<RenderedContent> PreviewAsync(PreviewTemplateRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var tenantRow = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, ct);
        var template = await resolver.ResolveAsync(tenantId, request.Code, request.Channel, request.Language, tenantRow.DefaultLanguage, ct)
                       ?? throw new NotFoundException("Template", $"{request.Code}/{request.Channel}/{request.Language}");
        return TemplateRenderer.Render(template, request.Data, request.Channel == NotificationChannel.Email);
    }

    private static TemplateDto ToDto(NotificationTemplate t) => new(t.Id, t.TenantId is null, t.Code, t.Channel, t.Language, t.Version,
        t.Subject, t.Body, t.PlaceholderNames, t.IsActive, t.CreatedAt, t.CreatedBy);
}
