using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationGateway.Application.Webhooks;
using NotificationGateway.Domain.Enums;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>
/// Fake provider receipts (the reference implementation of a signed, timestamped callback).
/// Headers: X-Fake-Timestamp = unix seconds; X-Fake-Signature = hex(HMAC-SHA256(secret, "{timestamp}.{body}")).
/// Signing the timestamp together with the body means an attacker cannot replay an old body with a fresh timestamp;
/// callbacks older than 5 minutes are rejected, and event ids are de-duplicated on top of that.
/// Body: { "events": [ { "eventId": "...", "providerMessageId": "...", "status": "delivered|failed", "reason": "..." } ] }
/// </summary>
public sealed class FakeWebhookHandler(NotificationChannel channel) : IProviderWebhookHandler
{
    public string ProviderName => FakeProvider.KeyFor(channel);

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public bool Verify(WebhookRequest request, string signingSecret)
    {
        var header = request.Header("X-Fake-Signature");
        var timestamp = request.Header("X-Fake-Timestamp");
        if (string.IsNullOrWhiteSpace(header) || !long.TryParse(timestamp, out var unix)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > MaxClockSkew.TotalSeconds) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes($"{timestamp}.{request.Body}"));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header)); }
        catch (FormatException) { return false; }
    }

    public IReadOnlyList<ProviderReceipt> Parse(WebhookRequest request)
    {
        using var doc = JsonDocument.Parse(request.Body);
        if (!doc.RootElement.TryGetProperty("events", out var events)) return Array.Empty<ProviderReceipt>();
        return events.EnumerateArray().Select(e => new ProviderReceipt(
            e.GetProperty("eventId").GetString()!,
            e.GetProperty("providerMessageId").GetString()!,
            (e.GetProperty("status").GetString() ?? "").ToLowerInvariant() switch
            {
                "delivered" => ReceiptStatus.Delivered,
                "failed" or "undelivered" or "bounced" => ReceiptStatus.Failed,
                _ => ReceiptStatus.Other
            },
            e.TryGetProperty("reason", out var r) ? r.GetString() : null,
            null)).ToList();
    }

    public static string Sign(string secret, string body, long unixTimestamp) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixTimestamp}.{body}")));
}

/// <summary>
/// Twilio status callback (application/x-www-form-urlencoded).
/// X-Twilio-Signature = base64(HMAC-SHA1(authToken, fullUrl + concat(sorted(key + value)))).
/// The webhook's signing secret is the Twilio auth token. Used for twilio-sms and twilio-whatsapp (same callback format).
/// </summary>
public sealed class TwilioWebhookHandler(string providerName = TwilioSmsProvider.Key) : IProviderWebhookHandler
{
    public string ProviderName => providerName;

    public bool Verify(WebhookRequest request, string signingSecret)
    {
        var header = request.Header("X-Twilio-Signature");
        if (string.IsNullOrWhiteSpace(header)) return false;

        var data = new StringBuilder(request.Url);
        foreach (var kv in request.Form.OrderBy(k => k.Key, StringComparer.Ordinal)) data.Append(kv.Key).Append(kv.Value);
#pragma warning disable CA5350 // Twilio mandates HMAC-SHA1 for this signature.
        var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(data.ToString()));
#pragma warning restore CA5350
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(header)); }
        catch (FormatException) { return false; }
    }

    public IReadOnlyList<ProviderReceipt> Parse(WebhookRequest request)
    {
        if (!request.Form.TryGetValue("MessageSid", out var sid) || !request.Form.TryGetValue("MessageStatus", out var status))
            return Array.Empty<ProviderReceipt>();

        var mapped = status switch
        {
            "delivered" => ReceiptStatus.Delivered,
            "undelivered" or "failed" => ReceiptStatus.Failed,
            _ => ReceiptStatus.Other   // queued, sent, sending: informational
        };
        request.Form.TryGetValue("ErrorCode", out var errorCode);
        return new[] { new ProviderReceipt($"{sid}:{status}", sid, mapped, errorCode is null ? null : $"Twilio error {errorCode}", null) };
    }
}

/// <summary>
/// SendGrid Event Webhook (signed). Signature = ECDSA P-256 over (timestamp + body), base64 DER, in
/// X-Twilio-Email-Event-Webhook-Signature; timestamp in X-Twilio-Email-Event-Webhook-Timestamp.
/// The webhook's signing secret is SendGrid's verification public key (base64 SubjectPublicKeyInfo).
/// </summary>
public sealed class SendGridWebhookHandler : IProviderWebhookHandler
{
    public string ProviderName => SendGridProvider.Key;

    public bool Verify(WebhookRequest request, string signingSecret)
    {
        var signature = request.Header("X-Twilio-Email-Event-Webhook-Signature");
        var timestamp = request.Header("X-Twilio-Email-Event-Webhook-Timestamp");
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp)) return false;

        // Reject stale callbacks (replay window) in addition to event-id de-duplication.
        if (long.TryParse(timestamp, out var unix) && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 600) return false;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signingSecret), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(timestamp + request.Body), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public IReadOnlyList<ProviderReceipt> Parse(WebhookRequest request)
    {
        using var doc = JsonDocument.Parse(request.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ProviderReceipt>();

        var list = new List<ProviderReceipt>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var messageId = e.TryGetProperty("sg_message_id", out var m) ? m.GetString() : null;
            var eventId = e.TryGetProperty("sg_event_id", out var id) ? id.GetString() : null;
            if (messageId is null || eventId is null) continue;

            var status = (e.TryGetProperty("event", out var ev) ? ev.GetString() : null) switch
            {
                "delivered" => ReceiptStatus.Delivered,
                "bounce" or "dropped" => ReceiptStatus.Failed,
                _ => ReceiptStatus.Other
            };
            DateTimeOffset? at = e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            var reason = e.TryGetProperty("reason", out var r) ? r.GetString() : null;

            // sg_message_id = "<X-Message-Id>.filter...": the prefix is what the send API returned.
            list.Add(new ProviderReceipt(eventId, messageId.Split('.')[0], status, reason, at));
        }
        return list;
    }
}

/// <summary>
/// WhatsApp Business Cloud API (Meta) status webhooks.
/// X-Hub-Signature-256 = "sha256=" + hex(HMAC-SHA256(appSecret, rawBody)); the webhook's signing secret is the Meta
/// app secret. Meta sends no signed timestamp, so replay protection relies on (message id + status) event de-duplication.
/// Subscription verification (GET hub.challenge) is handled by WebhookService using the webhook's public token.
/// </summary>
public sealed class MetaWhatsAppWebhookHandler : IProviderWebhookHandler
{
    public string ProviderName => MetaWhatsAppProvider.Key;

    public bool Verify(WebhookRequest request, string signingSecret)
    {
        var header = request.Header("X-Hub-Signature-256");
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(request.Body));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header[7..])); }
        catch (FormatException) { return false; }
    }

    public IReadOnlyList<ProviderReceipt> Parse(WebhookRequest request)
    {
        var list = new List<ProviderReceipt>();
        using var doc = JsonDocument.Parse(request.Body);
        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return list;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || !value.TryGetProperty("statuses", out var statuses)) continue;
                foreach (var st in statuses.EnumerateArray())
                {
                    var id = st.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var status = st.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (id is null || status is null) continue;

                    DateTimeOffset? at = st.TryGetProperty("timestamp", out var ts) && long.TryParse(ts.GetString(), out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
                    string? reason = null;
                    if (st.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                        reason = errors[0].TryGetProperty("title", out var t) ? t.GetString() : errors[0].GetRawText();

                    var mapped = status switch
                    {
                        "delivered" or "read" => ReceiptStatus.Delivered,
                        "failed" => ReceiptStatus.Failed,
                        _ => ReceiptStatus.Other
                    };
                    list.Add(new ProviderReceipt($"{id}:{status}", id, mapped, reason, at));
                }
            }
        }
        return list;
    }
}
