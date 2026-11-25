namespace NotificationGateway.Application.Common;

/// <summary>401: provider callback whose signature could not be verified.</summary>
public sealed class WebhookSignatureException(string message) : Exception(message);

/// <summary>Helpers shared by the application services.</summary>
public static class Guard
{
    public static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw RequestValidationException.For(field, $"{field} is required.");
    }

    public static void MaxLength(string? value, int max, string field)
    {
        if (value is not null && value.Length > max)
            throw RequestValidationException.For(field, $"{field} must be at most {max} characters.");
    }

    public static void TimeZone(string? timeZoneId, string field)
    {
        if (timeZoneId is null) return;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
            throw RequestValidationException.For(field, $"Unknown time zone '{timeZoneId}'.");
    }
}

/// <summary>Simple paging envelope.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
