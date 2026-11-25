namespace NotificationGateway.Domain.Enums;

/// <summary>Classification of one provider call. Drives retry / fallback decisions.</summary>
public enum AttemptOutcome
{
    /// <summary>Provider accepted the message.</summary>
    Accepted = 0,
    /// <summary>Failed before or without the provider accepting it, safe to retry/fail over (5xx, 429, connection refused).</summary>
    TransientFailure = 1,
    /// <summary>The provider configuration itself is broken (auth error, disabled account). Fail over to the next provider.</summary>
    ProviderFailure = 2,
    /// <summary>The recipient/content is invalid (bad phone number). Every provider would reject it, so no fail over.</summary>
    RecipientRejected = 3,
    /// <summary>The request was sent but we do not know if the provider processed it (read timeout, connection reset after send).</summary>
    Uncertain = 4,
    /// <summary>Skipped without calling the provider (circuit open, quota exceeded).</summary>
    Skipped = 5
}
