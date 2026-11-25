using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationGateway.Application.Abstractions;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Application.Settings;

public sealed record ProviderCatalogDto(
    string Key, NotificationChannel Channel, string DisplayName, ProviderCapabilities Capabilities, bool IsEnabled,
    IReadOnlyList<string> RequiredSettings, IReadOnlyList<string> RequiredSecrets, decimal? EstimatedCostPerMessage, string? CostCurrency);

/// <summary>Secrets are NEVER part of this DTO: only their names, version and rotation time.</summary>
public sealed record ProviderConfigurationDto(
    Guid Id, bool IsShared, string ProviderKey, NotificationChannel Channel, string Name, JsonElement Settings,
    IReadOnlyList<string> SecretNames, int SecretVersion, DateTimeOffset SecretsUpdatedAt, string? SenderIdentity,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateProviderConfigurationRequest(
    string ProviderKey, string Name, JsonElement? Settings, Dictionary<string, string>? Secrets, string? SenderIdentity, bool Shared = false);

public sealed record UpdateProviderConfigurationRequest(string Name, JsonElement? Settings, string? SenderIdentity, bool IsActive);

public sealed record RotateSecretsRequest(Dictionary<string, string> Secrets);

public sealed record ProviderChainItemDto(int Order, Guid ProviderConfigurationId, string ProviderKey, string Name, bool IsShared, bool IsActive);

public sealed record SetProviderChainRequest(IReadOnlyList<Guid> ProviderConfigurationIds);

public sealed record UpdateCatalogEntryRequest(bool IsEnabled, decimal? EstimatedCostPerMessage, string? CostCurrency);

/// <summary>
/// Provider configurations (tenant-owned or shared), their encrypted secrets, and the per-channel provider chain.
/// Isolation is enforced by the DbContext filters (reads) and write guard (writes); this service adds the business rules.
/// </summary>
public sealed class ProviderConfigurationService(
    IApplicationDbContext db,
    ITenantContext tenant,
    IProviderRegistry registry,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    IProviderEndpointValidator endpointValidator,
    TimeProvider clock)
{
    // ---------------------------------------------------------------- catalog

    public async Task<IReadOnlyList<ProviderCatalogDto>> CatalogAsync(CancellationToken ct)
    {
        var rows = await db.NotificationProviders.AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct);
        return rows.Select(p =>
        {
            registry.TryGet(p.Key, out var adapter);
            return new ProviderCatalogDto(p.Key, p.Channel, p.DisplayName, p.Capabilities, p.IsEnabled,
                adapter?.Descriptor.RequiredSettings ?? Array.Empty<string>(),
                adapter?.Descriptor.RequiredSecrets ?? Array.Empty<string>(),
                p.EstimatedCostPerMessage, p.CostCurrency);
        }).ToList();
    }

    public async Task UpdateCatalogEntryAsync(string key, UpdateCatalogEntryRequest request, CancellationToken ct)
    {
        var provider = await db.NotificationProviders.FirstOrDefaultAsync(p => p.Key == key, ct) ?? throw new NotFoundException("Provider", key);
        provider.SetEnabled(request.IsEnabled);
        provider.SetPricing(request.EstimatedCostPerMessage, request.CostCurrency);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- configurations

    public async Task<IReadOnlyList<ProviderConfigurationDto>> ListAsync(NotificationChannel? channel, CancellationToken ct)
    {
        var query = db.ProviderConfigurations.AsNoTracking();
        if (channel is not null) query = query.Where(c => c.Channel == channel);
        var rows = await query.OrderBy(c => c.Channel).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ProviderConfigurationDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.ProviderConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
              ?? throw new NotFoundException("ProviderConfiguration", id));

    public async Task<ProviderConfigurationDto> CreateAsync(CreateProviderConfigurationRequest request, CancellationToken ct)
    {
        Guard.Required(request.ProviderKey, nameof(request.ProviderKey));
        Guard.Required(request.Name, nameof(request.Name));
        Guard.MaxLength(request.Name, 128, nameof(request.Name));

        if (request.Shared && !tenant.IsSystemAdmin)
            throw new TenantAccessDeniedException("Only system administrators can create shared provider configurations.");

        Guid? ownerTenantId = request.Shared ? null : tenant.RequireTenantId();
        var adapter = await RequireUsableAdapterAsync(request.ProviderKey, ct);

        var settings = NormalizeSettings(request.Settings);
        var secrets = request.Secrets ?? new Dictionary<string, string>();
        ValidateWithAdapter(adapter, settings, secrets);
        await ValidateEndpointsAsync(settings, ct);

        var configuration = new TenantProviderConfiguration(ownerTenantId, adapter.ProviderName, adapter.Channel, request.Name.Trim(),
            settings.GetRawText(), encryptedSecrets: "pending", request.SenderIdentity, clock.GetUtcNow());

        // The envelope is bound to the row id (AAD), so it can only be produced once the id exists.
        configuration.ReplaceEnvelope(Protect(configuration.Id, secrets));

        db.ProviderConfigurations.Add(configuration);
        db.ProviderHealth.Add(new ProviderHealth(configuration.Id));
        await db.SaveChangesAsync(ct);
        return ToDto(configuration);
    }

    public async Task<ProviderConfigurationDto> UpdateAsync(Guid id, UpdateProviderConfigurationRequest request, CancellationToken ct)
    {
        Guard.Required(request.Name, nameof(request.Name));
        var configuration = await FindAsync(id, ct);
        var adapter = registry.Get(configuration.ProviderKey);
        var settings = NormalizeSettings(request.Settings);
        ValidateWithAdapter(adapter, settings, Unprotect(configuration));
        await ValidateEndpointsAsync(settings, ct);

        configuration.Update(request.Name.Trim(), settings.GetRawText(), request.SenderIdentity, request.IsActive, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return ToDto(configuration);
    }

    public async Task<ProviderConfigurationDto> RotateSecretsAsync(Guid id, RotateSecretsRequest request, CancellationToken ct)
    {
        var configuration = await FindAsync(id, ct);
        var adapter = registry.Get(configuration.ProviderKey);
        ValidateWithAdapter(adapter, JsonDocument.Parse(configuration.SettingsJson).RootElement, request.Secrets);

        configuration.RotateSecrets(Protect(configuration.Id, request.Secrets), clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return ToDto(configuration);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct)
    {
        var configuration = await FindAsync(id, ct);
        configuration.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-encrypts every envelope written with a retired master key (keeps value and version).</summary>
    public async Task<int> ReEncryptAllAsync(CancellationToken ct)
    {
        if (!tenant.IsSystemAdmin) throw new TenantAccessDeniedException("Only system administrators can re-encrypt secrets.");
        using var tenantScope = tenant.BeginSystemScope();   // must see every tenant's rows
        var count = 0;
        var rows = await db.ProviderConfigurations.ToListAsync(ct);
        foreach (var row in rows.Where(r => protector.NeedsReEncryption(r.EncryptedSecrets)))
        {
            var binding = SecretBindings.ProviderConfig(row.Id);
            row.ReplaceEnvelope(protector.Protect(protector.Unprotect(row.EncryptedSecrets, binding), binding));
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    // ---------------------------------------------------------------- provider chain (primary + fallbacks)

    public async Task<IReadOnlyList<ProviderChainItemDto>> GetChainAsync(NotificationChannel channel, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var rows = await db.ChannelProviderAssignments.AsNoTracking()
            .Include(a => a.ProviderConfiguration)
            .Where(a => a.Channel == channel)
            .OrderBy(a => a.Order)
            .ToListAsync(ct);

        return rows.Select(a => new ProviderChainItemDto(a.Order, a.ProviderConfigurationId, a.ProviderConfiguration.ProviderKey,
            a.ProviderConfiguration.Name, a.ProviderConfiguration.IsShared, a.ProviderConfiguration.IsActive)).ToList();
    }

    /// <summary>
    /// Replaces the chain. Every id must be visible to this tenant (its own or shared): another tenant's configuration is
    /// invisible through the query filter and therefore reported as not found (T08).
    /// </summary>
    public async Task<IReadOnlyList<ProviderChainItemDto>> SetChainAsync(NotificationChannel channel, SetProviderChainRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var ids = request.ProviderConfigurationIds ?? Array.Empty<Guid>();
        if (ids.Distinct().Count() != ids.Count)
            throw RequestValidationException.For(nameof(request.ProviderConfigurationIds), "A configuration can appear only once in the chain.");

        var configs = await db.ProviderConfigurations.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        foreach (var id in ids)
        {
            var config = configs.FirstOrDefault(c => c.Id == id) ?? throw new NotFoundException("ProviderConfiguration", id);
            if (config.Channel != channel)
                throw RequestValidationException.For(nameof(request.ProviderConfigurationIds), $"Configuration {id} is for {config.Channel}, not {channel}.");
            if (!config.IsActive)
                throw RequestValidationException.For(nameof(request.ProviderConfigurationIds), $"Configuration {id} is inactive.");
        }

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var existing = await db.ChannelProviderAssignments.Where(a => a.Channel == channel).ToListAsync(token);
            db.ChannelProviderAssignments.RemoveRange(existing);
            await db.SaveChangesAsync(token);   // deletes first: (TenantId, Channel, Order) is unique

            for (var i = 0; i < ids.Count; i++)
                db.ChannelProviderAssignments.Add(new ChannelProviderAssignment(tenantId, channel, ids[i], i));
            await db.SaveChangesAsync(token);
            return true;
        }, ct);

        return await GetChainAsync(channel, ct);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TenantProviderConfiguration> FindAsync(Guid id, CancellationToken ct) =>
        await db.ProviderConfigurations.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("ProviderConfiguration", id);

    private async Task<INotificationProvider> RequireUsableAdapterAsync(string providerKey, CancellationToken ct)
    {
        if (!registry.TryGet(providerKey, out var adapter))
            throw RequestValidationException.For("ProviderKey", $"Unknown provider '{providerKey}'.");

        var catalog = await db.NotificationProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Key == adapter.ProviderName, ct);
        if (catalog is { IsEnabled: false })
            throw RequestValidationException.For("ProviderKey", $"Provider '{providerKey}' is disabled by the platform.");
        return adapter;
    }

    private static JsonElement NormalizeSettings(JsonElement? settings)
    {
        if (settings is null || settings.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return JsonDocument.Parse("{}").RootElement;
        if (settings.Value.ValueKind != JsonValueKind.Object)
            throw RequestValidationException.For("Settings", "Settings must be a JSON object.");
        return settings.Value;
    }

    private static void ValidateWithAdapter(INotificationProvider adapter, JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        var errors = adapter.ValidateConfiguration(settings, secrets);
        if (errors.Count > 0)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["Configuration"] = errors.ToArray() });
    }

    private async Task ValidateEndpointsAsync(JsonElement settings, CancellationToken ct)
    {
        var errors = await endpointValidator.ValidateAsync(settings, ct);
        if (errors.Count > 0)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["Settings"] = errors.ToArray() });
    }

    private string Protect(Guid configurationId, IReadOnlyDictionary<string, string> secrets) =>
        protector.Protect(JsonSerializer.Serialize(secrets), SecretBindings.ProviderConfig(configurationId));

    private Dictionary<string, string> Unprotect(TenantProviderConfiguration configuration) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            protector.Unprotect(configuration.EncryptedSecrets, SecretBindings.ProviderConfig(configuration.Id))) ?? new();

    private ProviderConfigurationDto ToDto(TenantProviderConfiguration c)
    {
        // Only secret NAMES are exposed (decrypting is safe here; values never leave this method).
        IReadOnlyList<string> names;
        try { names = Unprotect(c).Keys.OrderBy(k => k).ToList(); }
        catch { names = Array.Empty<string>(); }

        return new ProviderConfigurationDto(c.Id, c.IsShared, c.ProviderKey, c.Channel, c.Name,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(c.SettingsJson) ? "{}" : c.SettingsJson).RootElement,
            names, c.SecretVersion, c.SecretsUpdatedAt, c.SenderIdentity, c.IsActive, c.CreatedAt, c.UpdatedAt);
    }
}
