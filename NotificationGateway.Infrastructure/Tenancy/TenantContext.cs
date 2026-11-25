using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;

namespace NotificationGateway.Infrastructure.Tenancy;

/// <summary>
/// Scoped (one per HTTP request / per background unit of work). Mutable only through
/// <see cref="SetFromAuthenticatedUser"/> (middleware) or the disposable scopes (workers).
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;
    private string? _userId;
    private bool _isSystemAdmin;
    private bool _isSystemScope;

    public Guid? TenantId => _tenantId;
    public string? UserId => _userId;
    public bool IsSystemAdmin => _isSystemAdmin;
    public bool IsSystemScope => _isSystemScope;

    public Guid RequireTenantId() =>
        _tenantId ?? throw new TenantAccessDeniedException("No tenant is associated with the current request.");

    /// <summary>Called once by TenantResolutionMiddleware with values taken from the validated JWT.</summary>
    public void SetFromAuthenticatedUser(Guid? tenantId, string? userId, bool isSystemAdmin)
    {
        _tenantId = tenantId;
        _userId = userId;
        _isSystemAdmin = isSystemAdmin;
        _isSystemScope = false;
    }

    public IDisposable BeginScope(Guid tenantId, string? userId = null)
    {
        var previous = (_tenantId, _userId, _isSystemScope);
        _tenantId = tenantId;
        _userId = userId ?? _userId;
        _isSystemScope = false;
        return new Restore(() => (_tenantId, _userId, _isSystemScope) = previous);
    }

    public IDisposable BeginSystemScope()
    {
        var previous = _isSystemScope;
        _isSystemScope = true;
        return new Restore(() => _isSystemScope = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
