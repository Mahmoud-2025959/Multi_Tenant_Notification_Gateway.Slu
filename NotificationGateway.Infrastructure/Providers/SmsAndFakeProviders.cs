using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>
/// Twilio Programmable Messaging (SMS) over HTTPS.
/// Settings: from (or SenderIdentity) OR messagingServiceSid, optional statusCallbackUrl, optional baseUrl (https).
/// Secrets: accountSid, authToken.
/// </summary>
public sealed partial class TwilioSmsProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "twilio-sms";
    public const string HttpClientName = "twilio";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Sms;
    public override ProviderDescriptor Descriptor { get; } = new("Twilio SMS",
        ProviderCapabilities.DeliveryReceipts | ProviderCapabilities.StatusQuery | ProviderCapabilities.SenderIdentity,
        Array.Empty<string>(), new[] { "accountSid", "authToken" });

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        string? Get(string n) => settings.TryGetProperty(n, out var v) ? v.GetString() : null;
        if (Get("from") is null && Get("messagingServiceSid") is null)
            yield return "Either setting 'from' or 'messagingServiceSid' is required.";
        if (Get("statusCallbackUrl") is { } cb && !IsHttpsUrl(cb)) yield return "Setting 'statusCallbackUrl' must be an https URL.";
        if (Get("baseUrl") is { } b && !IsHttpsUrl(b)) yield return "Setting 'baseUrl' must be an https URL.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Address) || !E164().IsMatch(message.Address))
            return NotificationResult.RecipientRejected("invalid_phone", "Phone number must be in E.164 format (e.g. +201001234567).");

        var cfg = message.Configuration;
        var sid = cfg.Secret("accountSid");
        var form = new Dictionary<string, string> { ["To"] = message.Address, ["Body"] = message.Body };
        if (cfg.Setting("messagingServiceSid") is { } mss) form["MessagingServiceSid"] = mss;
        else form["From"] = cfg.SenderIdentity ?? cfg.Setting("from") ?? string.Empty;
        if (cfg.Setting("statusCallbackUrl") is { } callback) form["StatusCallback"] = callback;

        var baseUrl = cfg.Setting("baseUrl") ?? "https://api.twilio.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/2010-04-01/Accounts/{sid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{cfg.Secret("authToken")}")));

        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                return NotificationResult.Accepted(doc.RootElement.TryGetProperty("sid", out var s) ? s.GetString() : null, (int)response.StatusCode);
            }

            // Twilio error codes 21211/21614/21610: invalid / not mobile / unsubscribed number → recipient problem.
            if ((int)response.StatusCode == 400 && TryGetTwilioCode(body) is 21211 or 21614 or 21610 or 21408)
                return NotificationResult.RecipientRejected($"twilio_{TryGetTwilioCode(body)}", body, 400);
            return ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return NotificationResult.Transient("connect_failed", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return NotificationResult.Uncertain("connection_lost", ex.Message);
        }
    }

    private static int? TryGetTwilioCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var code) ? code : null;
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}

/// <summary>
/// Deterministic fake provider for demos and tests (one instance per channel: fake-email, fake-sms, ...).
/// Settings:
///   mode: success | transient | provider-failure | reject | uncertain | hang   (default success)
///   failFirst: N  → the first N calls for a delivery fail transiently, then succeed (retry tests)
///   latencyMs: N  → artificial latency
/// Every accepted message is recorded in <see cref="Sent"/> so tests can assert exactly what was sent (and how often).
/// </summary>
public sealed class FakeProvider(NotificationChannel channel) : ProviderBase
{
    public static readonly ConcurrentQueue<NotificationMessage> Sent = new();
    private static readonly ConcurrentDictionary<Guid, int> CallsPerDelivery = new();

    public override string ProviderName => KeyFor(channel);
    public override NotificationChannel Channel => channel;
    public override ProviderDescriptor Descriptor { get; } = new($"Fake {channel}",
        ProviderCapabilities.DeliveryReceipts | ProviderCapabilities.SenderIdentity, Array.Empty<string>(), Array.Empty<string>());

    public static string KeyFor(NotificationChannel channel) => $"fake-{channel.ToString().ToLowerInvariant()}";

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (settings.TryGetProperty("mode", out var m) && m.GetString() is { } mode &&
            mode is not ("success" or "transient" or "provider-failure" or "reject" or "uncertain" or "hang"))
            yield return "Setting 'mode' must be success, transient, provider-failure, reject, uncertain or hang.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var cfg = message.Configuration;
        if (int.TryParse(cfg.Setting("latencyMs"), out var latency) && latency > 0) await Task.Delay(latency, ct);

        var call = CallsPerDelivery.AddOrUpdate(message.DeliveryId, 1, (_, n) => n + 1);
        if (int.TryParse(cfg.Setting("failFirst"), out var failFirst) && call <= failFirst)
            return NotificationResult.Transient("fake_transient", $"Fake transient failure {call}/{failFirst}.", 503);

        switch (cfg.Setting("mode") ?? "success")
        {
            case "transient": return NotificationResult.Transient("fake_transient", "Fake provider unavailable.", 503);
            case "provider-failure": return NotificationResult.ProviderFailure("fake_auth", "Fake provider rejected credentials.", 401);
            case "reject": return NotificationResult.RecipientRejected("fake_invalid_recipient", "Fake provider rejected the recipient.", 400);
            case "uncertain": return NotificationResult.Uncertain("fake_timeout", "Fake provider timed out after the request was sent.");
            case "hang":
                await Task.Delay(Timeout.Infinite, ct);   // the dispatcher's timeout cancels this
                break;
        }

        Sent.Enqueue(message);
        return NotificationResult.Accepted($"fake-{Guid.NewGuid():N}", 202);
    }

    public static void Reset()
    {
        Sent.Clear();
        CallsPerDelivery.Clear();
    }
}
