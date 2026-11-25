namespace NotificationGateway.Application.Common;

public static class SecretBindings
{
    public static string ProviderConfig(Guid configurationId) => $"provider-config:{configurationId:N}";
    public static string Webhook(Guid webhookId) => $"webhook:{webhookId:N}";
}
