using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Domain.Providers;

/// <summary>
/// Catalog row describing a provider PRODUCT the gateway has an adapter for (e.g. "smtp", "sendgrid", "twilio-sms").
/// Rows are synchronised at startup from the registered adapters' descriptors, so the catalog always matches the code.
/// Not tenant owned: it says what CAN be used, TenantProviderConfiguration says what a tenant DOES use.
/// </summary>
public sealed class NotificationProvider
{
    private NotificationProvider() { }

    public NotificationProvider(string key, NotificationChannel channel, string displayName, ProviderCapabilities capabilities)
    {
        Key = key;
        Channel = channel;
        DisplayName = displayName;
        Capabilities = capabilities;
        IsEnabled = true;
    }

    /// <summary>Primary key. Stable string such as "sendgrid".</summary>
    public string Key { get; private set; } = default!;
    public NotificationChannel Channel { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public ProviderCapabilities Capabilities { get; private set; }

    /// <summary>System admins can globally disable a product (e.g. vendor outage, contract ended).</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Optional cost per message used by the cost-estimation report.</summary>
    public decimal? EstimatedCostPerMessage { get; private set; }
    public string? CostCurrency { get; private set; }

    public void Sync(NotificationChannel channel, string displayName, ProviderCapabilities capabilities)
    {
        Channel = channel;
        DisplayName = displayName;
        Capabilities = capabilities;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void SetPricing(decimal? costPerMessage, string? currency)
    {
        EstimatedCostPerMessage = costPerMessage;
        CostCurrency = currency;
    }
}
