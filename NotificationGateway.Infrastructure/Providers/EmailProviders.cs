using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NotificationGateway.Application.Providers;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Providers;

namespace NotificationGateway.Infrastructure.Providers;

/// <summary>
/// SMTP via MailKit. Settings: host, port, security ("none" | "starttls" | "ssl"), from (or SenderIdentity).
/// Secrets (optional): username, password.
/// Classification: failure BEFORE the DATA command → safe to fail over; connection lost AFTER it → Uncertain.
/// </summary>
public sealed class SmtpProvider : ProviderBase
{
    public const string Key = "smtp";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Email;
    public override ProviderDescriptor Descriptor { get; } = new("SMTP",
        ProviderCapabilities.SenderIdentity, new[] { "host", "port" }, Array.Empty<string>());

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (settings.TryGetProperty("port", out var port) && !int.TryParse(port.ToString(), out _))
            yield return "Setting 'port' must be a number.";
        if (settings.TryGetProperty("security", out var sec) && sec.ToString() is { Length: > 0 } s && s is not ("none" or "starttls" or "ssl"))
            yield return "Setting 'security' must be none, starttls or ssl.";
        if (secrets.ContainsKey("username") != secrets.ContainsKey("password"))
            yield return "Secrets 'username' and 'password' must be provided together.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var cfg = message.Configuration;
        var from = cfg.SenderIdentity ?? cfg.Setting("from");
        if (string.IsNullOrWhiteSpace(from)) return NotificationResult.ProviderFailure("no_sender", "No From address configured.");
        if (string.IsNullOrWhiteSpace(message.Address) || !MailboxAddress.TryParse(message.Address, out var to))
            return NotificationResult.RecipientRejected("invalid_address", "Invalid e-mail address.");

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(from));
        mime.To.Add(to);
        mime.Subject = message.Subject ?? string.Empty;
        mime.MessageId = $"{message.IdempotencyKey}@notification-gateway";
        mime.Headers.Add("X-Notification-Delivery", message.DeliveryId.ToString());
        mime.Body = new BodyBuilder { HtmlBody = message.Body }.ToMessageBody();

        var security = cfg.Setting("security") switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };

        var dataStarted = false;
        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(cfg.Setting("host")!, int.Parse(cfg.Setting("port") ?? "25"), security, ct);
            if (cfg.Secret("username") is { } user)
                await client.AuthenticateAsync(user, cfg.Secret("password") ?? string.Empty, ct);

            dataStarted = true;
            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, CancellationToken.None);
            return NotificationResult.Accepted(mime.MessageId);
        }
        catch (OperationCanceledException) { throw; }
        catch (AuthenticationException ex) { return NotificationResult.ProviderFailure("auth_failed", ex.Message); }
        catch (SmtpCommandException ex) when (ex.ErrorCode is SmtpErrorCode.RecipientNotAccepted)
        {
            return NotificationResult.RecipientRejected("recipient_rejected", ex.Message, (int)ex.StatusCode);
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
        {
            return NotificationResult.ProviderFailure("smtp_permanent", ex.Message, (int)ex.StatusCode);
        }
        catch (SmtpCommandException ex) { return NotificationResult.Transient("smtp_temporary", ex.Message, (int)ex.StatusCode); }
        catch (Exception ex) when (ex is SocketException or SslHandshakeException && !dataStarted)
        {
            return NotificationResult.Transient("connect_failed", ex.Message);
        }
        catch (Exception ex) when (dataStarted && ex is IOException or ServiceNotConnectedException or SmtpProtocolException)
        {
            return NotificationResult.Uncertain("connection_lost", ex.Message);
        }
        catch (Exception ex) when (!dataStarted)
        {
            return NotificationResult.Transient("smtp_error", ex.Message);
        }
    }
}

/// <summary>
/// SendGrid v3 Mail Send over HTTPS (no SDK, so the adapter is fully testable with a fake HttpMessageHandler).
/// Settings: from (or SenderIdentity), optional baseUrl (https only). Secrets: apiKey.
/// </summary>
public sealed class SendGridProvider(IHttpClientFactory httpFactory) : ProviderBase
{
    public const string Key = "sendgrid";
    public const string HttpClientName = "sendgrid";
    public override string ProviderName => Key;
    public override NotificationChannel Channel => NotificationChannel.Email;
    public override ProviderDescriptor Descriptor { get; } = new("SendGrid",
        ProviderCapabilities.DeliveryReceipts | ProviderCapabilities.SenderIdentity, Array.Empty<string>(), new[] { "apiKey" });

    protected override IEnumerable<string> ValidateMore(JsonElement settings, IReadOnlyDictionary<string, string> secrets)
    {
        if (settings.TryGetProperty("baseUrl", out var url) && !IsHttpsUrl(url.GetString()))
            yield return "Setting 'baseUrl' must be an absolute https URL.";
    }

    public override async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var cfg = message.Configuration;
        var from = cfg.SenderIdentity ?? cfg.Setting("from");
        if (string.IsNullOrWhiteSpace(from)) return NotificationResult.ProviderFailure("no_sender", "No From address configured.");
        if (string.IsNullOrWhiteSpace(message.Address)) return NotificationResult.RecipientRejected("no_address", "Missing e-mail address.");

        var payload = new
        {
            personalizations = new[]
            {
                new { to = new[] { new { email = message.Address } }, custom_args = new { delivery_id = message.DeliveryId.ToString() } }
            },
            from = new { email = from },
            subject = message.Subject ?? string.Empty,
            content = new[] { new { type = "text/html", value = message.Body } }
        };

        var baseUrl = cfg.Setting("baseUrl") ?? "https://api.sendgrid.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v3/mail/send")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Secret("apiKey"));

        var requestSent = false;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            requestSent = true;
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var id = response.Headers.TryGetValues("X-Message-Id", out var values) ? values.FirstOrDefault() : null;
                return NotificationResult.Accepted(id, (int)response.StatusCode);
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            return ClassifyHttpFailure(response.StatusCode, body, badRequestMeansRecipient: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return NotificationResult.Transient("connect_failed", ex.Message);   // never reached SendGrid
        }
        catch (HttpRequestException ex) when (requestSent)
        {
            return NotificationResult.Uncertain("connection_lost", ex.Message);
        }
    }
}
