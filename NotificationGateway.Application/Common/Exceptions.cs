namespace NotificationGateway.Application.Common;

/// <summary>403: the caller tried to act on another tenant's resource or has no tenant.</summary>
public sealed class TenantAccessDeniedException(string message) : Exception(message);

/// <summary>
/// 404. Also used when a resource EXISTS but belongs to another tenant, so tenants cannot probe for other tenants' ids.
/// </summary>
public sealed class NotFoundException(string resource, object key) : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>400 with field errors.</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static RequestValidationException For(string field, string error) =>
        new(new Dictionary<string, string[]> { [field] = new[] { error } });
}

/// <summary>409: e.g. same idempotency key reused with a different payload.</summary>
public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>429: tenant quota exhausted and the tenant policy is Reject.</summary>
public sealed class QuotaExceededException(string message) : Exception(message);
