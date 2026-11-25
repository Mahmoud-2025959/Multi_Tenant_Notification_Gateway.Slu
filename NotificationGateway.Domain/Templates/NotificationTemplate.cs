using System.Text.RegularExpressions;
using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Templates;

/// <summary>
/// One immutable version of a template for (tenant|shared, code, channel, language).
/// Editing a template creates a NEW version; old versions stay for audit and for notifications already queued.
/// Resolution order: tenant template (requested language) -> tenant template (tenant default language)
///                   -> shared template (requested language) -> shared template (default language).
/// </summary>
public sealed partial class NotificationTemplate : Entity, IOptionallyTenantOwned
{
    private NotificationTemplate() { }

    public NotificationTemplate(
        Guid? tenantId,
        string code,
        NotificationChannel channel,
        TemplateLanguage language,
        int version,
        string? subject,
        string body,
        DateTimeOffset now,
        string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("template.code_required", "Template code is required.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("template.body_required", "Template body is required.");
        if (version < 1) throw new DomainException("template.version", "Version must be >= 1.");

        TenantId = tenantId;
        Code = code.Trim().ToUpperInvariant();
        Channel = channel;
        Language = language;
        Version = version;
        Subject = subject;
        Body = body;
        Placeholders = string.Join(',', ExtractPlaceholders(subject, body));
        IsActive = true;
        CreatedAt = now;
        CreatedBy = createdBy;
    }

    public Guid? TenantId { get; private set; }
    public string Code { get; private set; } = default!;
    public NotificationChannel Channel { get; private set; }
    public TemplateLanguage Language { get; private set; }
    public int Version { get; private set; }
    public string? Subject { get; private set; }
    public string Body { get; private set; } = default!;

    /// <summary>Comma separated placeholder names found in subject/body, computed on creation (used for validation).</summary>
    public string Placeholders { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }

    public IReadOnlyCollection<string> PlaceholderNames =>
        Placeholders.Length == 0 ? Array.Empty<string>() : Placeholders.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;

    public static IReadOnlyList<string> ExtractPlaceholders(string? subject, string body)
    {
        var text = (subject ?? string.Empty) + "\n" + body;
        return PlaceholderRegex().Matches(text)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant)]
    public static partial Regex PlaceholderRegex();
}
