using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Common;
using NotificationGateway.Domain.Common;

namespace Multi_Tenant_Notification_Gateway.Middleware;

/// <summary>Maps application/domain exceptions to RFC 7807 problem responses. Never leaks stack traces or secrets.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, code) = exception switch
        {
            RequestValidationException => (400, "Validation failed", "validation"),
            DomainException d => (400, "Business rule violated", d.Code),
            NotFoundException => (404, "Not found", "not_found"),
            TenantAccessDeniedException => (403, "Forbidden", "tenant_access_denied"),
            WebhookSignatureException => (401, "Invalid signature", "invalid_signature"),
            ConflictException c => (409, "Conflict", c.Code),
            DbUpdateConcurrencyException => (409, "Conflict", "concurrency"),
            QuotaExceededException => (429, "Quota exceeded", "quota_exceeded"),
            _ => (500, "Unexpected error", "server_error")
        };

        if (status == 500) logger.LogError(exception, "Unhandled exception");
        else logger.LogInformation("Request failed with {Status} {Code}: {Message}", status, code, exception.Message);

        ProblemDetails problem = exception is RequestValidationException v
            ? new ValidationProblemDetails(v.Errors) { Status = status, Title = title }
            : new ProblemDetails { Status = status, Title = title, Detail = status == 500 && !env.IsDevelopment() ? null : exception.Message };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", ct);
        return true;
    }
}
