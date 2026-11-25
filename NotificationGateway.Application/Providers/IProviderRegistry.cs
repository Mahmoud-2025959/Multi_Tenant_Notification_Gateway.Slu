using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Application.Providers;

/// <summary>Resolves adapters by key. Replaces any switch/if-else on provider names.</summary>
public interface IProviderRegistry
{
    bool TryGet(string providerKey, out INotificationProvider provider);
    INotificationProvider Get(string providerKey);
    IReadOnlyCollection<INotificationProvider> All { get; }
    IReadOnlyCollection<INotificationProvider> ForChannel(NotificationChannel channel);
}

internal sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyDictionary<string, INotificationProvider> _providers;

    public ProviderRegistry(IEnumerable<INotificationProvider> providers)
    {
        var dict = new Dictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!dict.TryAdd(provider.ProviderName, provider))
                throw new InvalidOperationException($"Provider key '{provider.ProviderName}' is registered twice.");
        }
        _providers = dict;
    }

    public IReadOnlyCollection<INotificationProvider> All => _providers.Values.ToList();

    public bool TryGet(string providerKey, out INotificationProvider provider) =>
        _providers.TryGetValue(providerKey, out provider!);

    public INotificationProvider Get(string providerKey) =>
        TryGet(providerKey, out var p) ? p : throw new KeyNotFoundException($"No adapter registered for provider '{providerKey}'.");

    public IReadOnlyCollection<INotificationProvider> ForChannel(NotificationChannel channel) =>
        _providers.Values.Where(p => p.Channel == channel).ToList();
}
