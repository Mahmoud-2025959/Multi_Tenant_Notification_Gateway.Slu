using System.Net;
using System.Text.Json;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>Common checks + HTTP status classification shared by adapters.</summary>
public abstract class ProviderBase : INotificationProvider
{
    public abstract string ProviderName { get; }
    public abstract NotificationChannel Channel { get; }
    public abstract ProviderDescriptor Descriptor { get; }

    public virtual IReadOnlyList<string> ValidateConfiguration(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        var errors = new List<string>();
        foreach (var name in Descriptor.RequiredSettings)
        {
            if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(name, out var v) ||
                v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                errors.Add($"Setting '{name}' is required.");
        }
        foreach (var name in Descriptor.RequiredSecrets)
        {
            if (!secrets.TryGetValue(name, out var s) || string.IsNullOrWhiteSpace(s))
                errors.Add($"Secret '{name}' is required.");
        }
        errors.AddRange(ValidateMore(settings, secrets));
        return errors;
    }

    protected virtual IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets) => Array.Empty<string>();

    public abstract Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);

    /// <summary>Public entry point for shared helpers (same rules as <see cref="ClassifyHttpFailure"/>).</summary>
    public static NotificationResult Classify(HttpStatusCode status, string body, bool badRequestMeansRecipient) =>
        ClassifyHttpFailure(status, body, badRequestMeansRecipient);

    /// <summary>Generic HTTP classification: 429/5xx retry elsewhere, 401/403 broken config, other 4xx bad request.</summary>
    protected static NotificationResult ClassifyHttpFailure(HttpStatusCode status, string body, bool badRequestMeansRecipient)
    {
        var code = (int)status;
        var text = body.Length > 500 ? body[..500] : body;
        return code switch
        {
            429 => NotificationResult.Transient("rate_limited", text, code),
            >= 500 => NotificationResult.Transient("provider_unavailable", text, code),
            401 or 403 => NotificationResult.ProviderFailure("auth_failed", text, code),
            >= 400 when badRequestMeansRecipient => NotificationResult.RecipientRejected("rejected", text, code),
            _ => NotificationResult.ProviderFailure("unexpected_status", text, code)
        };
    }

    protected static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
