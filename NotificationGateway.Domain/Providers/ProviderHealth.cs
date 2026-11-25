using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Providers;

/// <summary>
/// Health + circuit breaker state per provider configuration. Shared across API instances because it is in the DB.
/// </summary>
public sealed class ProviderHealth
{
    private ProviderHealth() { }

    public ProviderHealth(Guid providerConfigurationId) => ProviderConfigurationId = providerConfigurationId;

    public Guid ProviderConfigurationId { get; private set; }
    public ProviderCircuitState State { get; private set; } = ProviderCircuitState.Closed;
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? OpenUntil { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastError { get; private set; }
    public long TotalCalls { get; private set; }
    public long TotalFailures { get; private set; }
    public long TotalLatencyMs { get; private set; }

    public bool AllowsCall(DateTimeOffset now) => State switch
    {
        ProviderCircuitState.Closed => true,
        ProviderCircuitState.HalfOpen => true,
        ProviderCircuitState.Open => OpenUntil is null || now >= OpenUntil,
        _ => true
    };

    public void RecordSuccess(long latencyMs, DateTimeOffset now)
    {
        TotalCalls++;
        TotalLatencyMs += latencyMs;
        ConsecutiveFailures = 0;
        State = ProviderCircuitState.Closed;
        OpenUntil = null;
        LastSuccessAt = now;
    }

    public void RecordFailure(long latencyMs, string error, DateTimeOffset now, int failureThreshold, TimeSpan openDuration)
    {
        TotalCalls++;
        TotalFailures++;
        TotalLatencyMs += latencyMs;
        ConsecutiveFailures++;
        LastFailureAt = now;
        LastError = error.Length > 500 ? error[..500] : error;

        if (State == ProviderCircuitState.HalfOpen || ConsecutiveFailures >= failureThreshold)
        {
            State = ProviderCircuitState.Open;
            OpenUntil = now.Add(openDuration);
        }
    }

    /// <summary>Called when an Open circuit's cool-down has elapsed and we let one probe call through.</summary>
    public void MoveToHalfOpen() => State = ProviderCircuitState.HalfOpen;
}
