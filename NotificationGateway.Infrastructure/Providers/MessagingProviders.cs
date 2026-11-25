using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>Shared HTTP plumbing for JSON/form provider APIs: one place for the "did the request leave us?" rules.</summary>
internal static class ProviderHttp
{
    public static async Task<(HttpResponseMessage? Response, string Body, NotificationResult? Failure)> SendAsync(
        IHttpClientFactory factory, string clientName, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await factory.CreateClient(clientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response, body, null);
        }
        catch (OperationCanceledException) { throw; }   // the dispatcher turns its own timeout into Uncertain
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return (null, string.Empty, NotificationResult.Transient("connect_failed", ex.Message));   // never reached the provider
        }
        catch (HttpRequestException ex)
        {
            return (null, string.Empty, NotificationResult.Uncertain("connection_lost", ex.Message));    // may have reached it
        }
    }

    public static string? JsonString(string body, params string[] path)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement;
            foreach (var p in path)
            {
                if (int.TryParse(p, out var i))
                {
                    if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return null;
                    e = e[i];
                }
                else if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind is JsonValueKind.Number ? e.GetRawText() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Convention for template-based channels (WhatsApp business templates): each non-empty line of the rendered gateway
    /// template body is one positional parameter ({{1}}, {{2}}, ...) of the provider-approved template.
    /// </summary>
    public static string[] Lines(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool LooksArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');

    public static string? StringSetting(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// =====================================================================================================================
// WhatsApp
// =====================================================================================================================

/// <summary>
/// WhatsApp Business Cloud API (Meta Graph API).
/// Settings: phoneNumberId, apiVersion (default v20.0), messageType = "template" (default) | "text", languageCode (optional),
///           templateName (optional fallback), baseUrl (optional, https).
/// Secrets:  accessToken.
/// Template mode (business-initiated messages need a pre-approved template): the gateway template SUBJECT is the approved
/// Meta template name, and each line of the BODY is one positional parameter. Text mode sends the body as free text
/// (only allowed inside the 24-hour customer service window).
/// </summary>
public sealed class MetaWhatsAppProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "meta-whatsapp";
    public const string HttpClientName = "meta";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;
    public override ProviderDescriptor Descriptor { get; } = new("WhatsApp Business Cloud API (Meta)",
        ProviderCapabilities.DeliveryReceipts, new[] { "phoneNumberId" }, new[] { "accessToken" });

    // Meta error codes that mean "this recipient cannot get this message" (another provider would fail the same way).
    private static readonly HashSet<string> RecipientErrors = new() { "131026", "131030", "131051", "131047", "100" };

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (ProviderHttp.StringSetting(settings, "messageType") is { } type && type is not ("template" or "text"))
            yield return "Setting 'messageType' must be 'template' or 'text'.";
        if (ProviderHttp.StringSetting(settings, "baseUrl") is { } url && !IsHttpsUrl(url))
            yield return "Setting 'baseUrl' must be an https URL.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address))
            return NotificationResult.RecipientRejected("no_phone", "Recipient has no phone number.");

        var cfg = message.Configuration;
        var to = message.Address.Replace("+", string.Empty).Trim();
        var language = cfg.Setting("languageCode") ?? (ProviderHttp.LooksArabic(message.Body) ? "ar" : "en");

        object payload;
        if ((cfg.Setting("messageType") ?? "template") == "text")
        {
            payload = new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "text", text = new { body = message.Body } };
        }
        else
        {
            var templateName = message.Subject ?? cfg.Setting("templateName");
            if (string.IsNullOrWhiteSpace(templateName))
                return NotificationResult.ProviderFailure("no_template", "No WhatsApp template name (template subject or 'templateName' setting).");

            payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = language },
                    components = new[]
                    {
                        new { type = "body", parameters = ProviderHttp.Lines(message.Body).Select(p => new { type = "text", text = p }).ToArray() }
                    }
                }
            };
        }

        var baseUrl = cfg.Setting("baseUrl") ?? "https://graph.facebook.com";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/{cfg.Setting("apiVersion") ?? "v20.0"}/{cfg.Setting("phoneNumberId")}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Secret("accessToken"));

        var (response, body, failure) = await ProviderHttp.SendAsync(httpFactory, HttpClientName, request, ct);
        if (failure is not null) return failure;

        if (response!.IsSuccessStatusCode)
            return NotificationResult.Accepted(ProviderHttp.JsonString(body, "messages", "0", "id"), (int)response.StatusCode);

        var code = ProviderHttp.JsonString(body, "error", "code");
        if (code is not null && RecipientErrors.Contains(code))
            return NotificationResult.RecipientRejected($"meta_{code}", ProviderHttp.JsonString(body, "error", "message") ?? body, (int)response.StatusCode);
        if (code == "190") return NotificationResult.ProviderFailure("meta_token_invalid", "Access token expired or invalid.", (int)response.StatusCode);
        return ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: false);
    }
}

/// <summary>Twilio Messages API shared by Twilio SMS and Twilio WhatsApp (same endpoint, auth, errors and receipts).</summary>
internal static class TwilioMessaging
{
    public const string HttpClientName = "twilio";

    private static readonly HashSet<string> RecipientErrors = new() { "21211", "21614", "21610", "21408", "63003", "63024" };

    public static async Task<NotificationResult> SendAsync(IHttpClientFactory factory, ProviderConfigurationContext cfg,
        Dictionary<string, string> form, CancellationToken ct)
    {
        var sid = cfg.Secret("accountSid");
        if (cfg.Setting("statusCallbackUrl") is { } callback) form["StatusCallback"] = callback;

        var baseUrl = cfg.Setting("baseUrl") ?? "https://api.twilio.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/2010-04-01/Accounts/{sid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{cfg.Secret("authToken")}")));

        var (response, body, failure) = await ProviderHttp.SendAsync(factory, HttpClientName, request, ct);
        if (failure is not null) return failure;

        if (response!.IsSuccessStatusCode)
            return NotificationResult.Accepted(ProviderHttp.JsonString(body, "sid"), (int)response.StatusCode);

        var code = ProviderHttp.JsonString(body, "code");
        if ((int)response.StatusCode == 400 && code is not null && RecipientErrors.Contains(code))
            return NotificationResult.RecipientRejected($"twilio_{code}", ProviderHttp.JsonString(body, "message") ?? body, 400);
        return ProviderBase.Classify(response.StatusCode, body, badRequestMeansRecipient: false);
    }
}

/// <summary>
/// Twilio WhatsApp. Settings: from (whatsapp sender number, or SenderIdentity), optional contentSid (approved template:
/// each body line becomes ContentVariables {"1":...,"2":...}), statusCallbackUrl, baseUrl. Secrets: accountSid, authToken.
/// </summary>
public sealed class TwilioWhatsAppProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "twilio-whatsapp";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;
    public override ProviderDescriptor Descriptor { get; } = new("Twilio WhatsApp",
        ProviderCapabilities.DeliveryReceipts | ProviderCapabilities.StatusQuery | ProviderCapabilities.SenderIdentity,
        Array.Empty<string>(), new[] { "accountSid", "authToken" });

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (ProviderHttp.StringSetting(settings, "from") is null) yield return "Setting 'from' (WhatsApp sender number) is required.";
    }

    public override Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address))
            return Task.FromResult(NotificationResult.RecipientRejected("no_phone", "Recipient has no phone number."));

        var cfg = message.Configuration;
        static string Wa(string n) => n.StartsWith("whatsapp:") ? n : $"whatsapp:{n}";
        var form = new Dictionary<string, string>
        {
            ["To"] = Wa(message.Address),
            ["From"] = Wa(cfg.SenderIdentity ?? cfg.Setting("from") ?? string.Empty)
        };

        if (cfg.Setting("contentSid") is { } contentSid)
        {
            var lines = ProviderHttp.Lines(message.Body);
            form["ContentSid"] = contentSid;
            form["ContentVariables"] = JsonSerializer.Serialize(lines.Select((v, i) => (Key: (i + 1).ToString(), v)).ToDictionary(x => x.Key, x => x.v));
        }
        else
        {
            form["Body"] = message.Body;
        }
        return TwilioMessaging.SendAsync(httpFactory, cfg, form, ct);
    }
}

// =====================================================================================================================
// SMS: local gateway
// =====================================================================================================================

/// <summary>
/// Generic local (Egyptian) SMS gateway over HTTPS JSON — the adapter ported from the Attendance System.
/// Settings: url (required), sender (or SenderIdentity), apiKeyHeader (default X-API-KEY), messageIdField (default "messageId").
/// Secrets: apiKey (optional). Encoding is chosen automatically: GSM-7 for Latin text, UCS-2 for Arabic.
/// No delivery receipts: the best status this provider can reach is Sent.
/// </summary>
public sealed class LocalSmsGatewayProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "local-sms";
    public const string HttpClientName = "local-sms";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Sms;
    public override ProviderDescriptor Descriptor { get; } = new("Local SMS gateway (HTTP)",
        ProviderCapabilities.SenderIdentity, new[] { "url" }, Array.Empty<string>());

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (ProviderHttp.StringSetting(settings, "url") is { } url && !Uri.TryCreate(url, UriKind.Absolute, out _))
            yield return "Setting 'url' must be an absolute URL.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address))
            return NotificationResult.RecipientRejected("no_phone", "Recipient has no phone number.");

        var cfg = message.Configuration;
        var unicode = SmsEncoding.RequiresUnicode(message.Body);
        var payload = new
        {
            to = message.Address,
            message = message.Body,
            sender = cfg.SenderIdentity ?? cfg.Setting("sender"),
            encoding = unicode ? "UCS2" : "GSM7",
            segments = SmsEncoding.Segments(message.Body),
            reference = message.IdempotencyKey
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, cfg.Setting("url"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (cfg.Secret("apiKey") is { } apiKey) request.Headers.TryAddWithoutValidation(cfg.Setting("apiKeyHeader") ?? "X-API-KEY", apiKey);

        var (response, body, failure) = await ProviderHttp.SendAsync(httpFactory, HttpClientName, request, ct);
        if (failure is not null) return failure;

        return response!.IsSuccessStatusCode
            ? NotificationResult.Accepted(ProviderHttp.JsonString(body, cfg.Setting("messageIdField") ?? "messageId"), (int)response.StatusCode)
            : ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: true);
    }
}

/// <summary>GSM 03.38 vs UCS-2 segment calculation (160/153 vs 70/67 characters), ported from the Attendance System.</summary>
public static class SmsEncoding
{
    private const string Gsm7 = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extended = "^{}\\[~]|€";

    public static bool RequiresUnicode(string text) => text.Any(c => !Gsm7.Contains(c) && !Gsm7Extended.Contains(c));

    public static int Segments(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        if (RequiresUnicode(text)) return text.Length <= 70 ? 1 : (int)Math.Ceiling(text.Length / 67.0);
        var length = text.Sum(c => Gsm7Extended.Contains(c) ? 2 : 1);
        return length <= 160 ? 1 : (int)Math.Ceiling(length / 153.0);
    }
}

// =====================================================================================================================
// Push: OneSignal
// =====================================================================================================================

/// <summary>
/// OneSignal push (REST API). Settings: appId, baseUrl (optional, https). Secrets: restApiKey.
/// The recipient's deviceToken is used as the OneSignal subscription id. OneSignal de-duplicates on "idempotency_key"
/// for 30 days, so an uncertain result can be retried on the same provider without a duplicate push (IdempotencyKey).
/// </summary>
public sealed class OneSignalPushProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "onesignal";
    public const string HttpClientName = "onesignal";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Push;
    public override ProviderDescriptor Descriptor { get; } = new("OneSignal push",
        ProviderCapabilities.IdempotencyKey, new[] { "appId" }, new[] { "restApiKey" });

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address))
            return NotificationResult.RecipientRejected("no_device", "Recipient has no device token.");

        var cfg = message.Configuration;
        var lang = ProviderHttp.LooksArabic(message.Body) ? "ar" : "en";
        var payload = new Dictionary<string, object?>
        {
            ["app_id"] = cfg.Setting("appId"),
            ["include_subscription_ids"] = new[] { message.Address },
            ["target_channel"] = "push",
            ["headings"] = new Dictionary<string, string> { ["en"] = message.Subject ?? message.NotificationType, [lang] = message.Subject ?? message.NotificationType },
            ["contents"] = new Dictionary<string, string> { ["en"] = message.Body, [lang] = message.Body },
            ["idempotency_key"] = message.DeliveryId.ToString(),
            ["priority"] = message.Priority >= NotificationPriority.High ? 10 : 5
        };

        var baseUrl = cfg.Setting("baseUrl") ?? "https://api.onesignal.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/notifications")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Key {cfg.Secret("restApiKey")}");

        var (response, body, failure) = await ProviderHttp.SendAsync(httpFactory, HttpClientName, request, ct);
        if (failure is not null) return failure;
        if (!response!.IsSuccessStatusCode) return ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: false);

        // 200 with an empty id means "no subscribed recipient" for this subscription id.
        var id = ProviderHttp.JsonString(body, "id");
        return string.IsNullOrEmpty(id)
            ? NotificationResult.RecipientRejected("not_subscribed", body.Length > 300 ? body[..300] : body, (int)response.StatusCode)
            : NotificationResult.Accepted(id, (int)response.StatusCode);
    }
}

// =====================================================================================================================
// Voice: Twilio Programmable Voice
// =====================================================================================================================

/// <summary>
/// Voice call that reads the rendered body (text-to-speech). Settings: from (or SenderIdentity), voiceLanguage (optional:
/// default ar-EG for Arabic text, en-US otherwise), statusCallbackUrl, baseUrl. Secrets: accountSid, authToken.
/// </summary>
public sealed partial class TwilioVoiceProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "twilio-voice";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Voice;
    public override ProviderDescriptor Descriptor { get; } = new("Twilio Voice (text-to-speech)",
        ProviderCapabilities.SenderIdentity | ProviderCapabilities.StatusQuery, Array.Empty<string>(), new[] { "accountSid", "authToken" });

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (ProviderHttp.StringSetting(settings, "from") is null) yield return "Setting 'from' (caller number) is required.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address) || !E164().IsMatch(message.Address))
            return NotificationResult.RecipientRejected("invalid_phone", "Phone number must be in E.164 format.");

        var cfg = message.Configuration;
        var language = cfg.Setting("voiceLanguage") ?? (ProviderHttp.LooksArabic(message.Body) ? "ar-EG" : "en-US");
        var twiml = $"<Response><Say language=\"{SecurityElement.Escape(language)}\">{SecurityElement.Escape(message.Body)}</Say></Response>";

        var sid = cfg.Secret("accountSid");
        var form = new Dictionary<string, string>
        {
            ["To"] = message.Address,
            ["From"] = cfg.SenderIdentity ?? cfg.Setting("from") ?? string.Empty,
            ["Twiml"] = twiml
        };
        if (cfg.Setting("statusCallbackUrl") is { } callback) form["StatusCallback"] = callback;

        var baseUrl = cfg.Setting("baseUrl") ?? "https://api.twilio.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/2010-04-01/Accounts/{sid}/Calls.json")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{cfg.Secret("authToken")}")));

        var (response, body, failure) = await ProviderHttp.SendAsync(httpFactory, TwilioMessaging.HttpClientName, request, ct);
        if (failure is not null) return failure;
        return response!.IsSuccessStatusCode
            ? NotificationResult.Accepted(ProviderHttp.JsonString(body, "sid"), (int)response.StatusCode)
            : ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: false);
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}
