using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NotificationGateway.Application.Common;
using NotificationGateway.Application.Dispatching;
using NotificationGateway.Application.Templates;
using NotificationGateway.Application.Webhooks;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Routing;
using NotificationGateway.Domain.Templates;
using NotificationGateway.Infrastructure.Providers;
using Xunit;

namespace NotificationGateway.UnitTests.Application;

public sealed class TemplateRendererTests
{
    private static NotificationTemplate Template(NotificationChannel channel, string? subject, string body) =>
        new(null, "TEST", channel, TemplateLanguage.En, 1, subject, body, DateTimeOffset.UtcNow, "test");

    private static JsonElement Data(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Replaces_placeholders_in_subject_and_body()
    {
        var result = TemplateRenderer.Render(Template(NotificationChannel.Sms, "Hi {{Name}}", "{{ Name }} at {{Time}}"),
            Data(new { Name = "Mona", Time = "08:00" }), htmlEncodeValues: false);

        result.Subject.Should().Be("Hi Mona");
        result.Body.Should().Be("Mona at 08:00");
    }

    [Fact]
    public void Missing_values_are_reported_by_name()
    {
        var act = () => TemplateRenderer.Render(Template(NotificationChannel.Sms, null, "{{A}} {{B}}"), Data(new { A = "x" }), false);
        act.Should().Throw<RequestValidationException>().Which.Errors["Data"].Single().Should().Contain("B");
    }

    [Fact]
    public void Email_values_are_html_encoded_to_prevent_injection()
    {
        var result = TemplateRenderer.Render(Template(NotificationChannel.Email, "S", "<p>{{Name}}</p>"),
            Data(new { Name = "<script>alert(1)</script>" }), htmlEncodeValues: true);
        result.Body.Should().Be("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>");
    }

    [Fact]
    public void Arabic_text_is_preserved()
    {
        var result = TemplateRenderer.Render(Template(NotificationChannel.Sms, null, "تأخير: {{Name}}"), Data(new { Name = "أحمد" }), false);
        result.Body.Should().Be("تأخير: أحمد");
    }

    [Fact]
    public void Placeholders_do_not_execute_expressions()
    {
        var result = TemplateRenderer.Render(Template(NotificationChannel.Sms, null, "{{A}}"), Data(new { A = "{{B}}" }), false);
        result.Body.Should().Be("{{B}}");   // values are never re-rendered
    }
}

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(10, 900)]   // capped at max
    public void Backoff_is_exponential_with_bounded_jitter(int round, double expectedSeconds)
    {
        var delay = RetryPolicy.Backoff(round, baseSeconds: 10, maxSeconds: 900, new Random(42));
        delay.TotalSeconds.Should().BeInRange(expectedSeconds * 0.8, expectedSeconds * 1.2);
    }
}

public sealed class RoutingRuleTests
{
    [Fact]
    public void Rule_matches_type_and_minimum_priority()
    {
        var rule = new NotificationRoutingRule(Guid.NewGuid(), "r", "attendance.late", NotificationPriority.High,
            new[] { NotificationChannel.Sms }, 1, false);

        rule.Matches("attendance.late", NotificationPriority.Critical).Should().BeTrue();
        rule.Matches("ATTENDANCE.LATE", NotificationPriority.High).Should().BeTrue();
        rule.Matches("attendance.late", NotificationPriority.Normal).Should().BeFalse();
        rule.Matches("leave.approved", NotificationPriority.Critical).Should().BeFalse();
    }
}

public sealed class WebhookSignatureTests
{
    private static WebhookRequest Request(string body, Dictionary<string, string> headers, Dictionary<string, string>? form = null, string url = "https://gw/api/webhooks/x/t") =>
        new(url, headers, body, form ?? new Dictionary<string, string>());

    [Fact]
    public void Fake_handler_accepts_valid_and_rejects_tampered_signature()
    {
        var handler = new FakeWebhookHandler(NotificationChannel.Sms);
        const string body = """{"events":[]}""";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = FakeWebhookHandler.Sign("secret", body, now);
        Dictionary<string, string> Headers(string signature, long ts) => new() { ["X-Fake-Signature"] = signature, ["X-Fake-Timestamp"] = ts.ToString() };

        handler.Verify(Request(body, Headers(sig, now)), "secret").Should().BeTrue();
        handler.Verify(Request(body + " ", Headers(sig, now)), "secret").Should().BeFalse();          // tampered body
        handler.Verify(Request(body, Headers(sig, now)), "other-secret").Should().BeFalse();         // wrong secret
        handler.Verify(Request(body, Headers(sig, now + 1)), "secret").Should().BeFalse();            // timestamp is signed too
        var old = now - 3600;
        handler.Verify(Request(body, Headers(FakeWebhookHandler.Sign("secret", body, old), old)), "secret").Should().BeFalse(); // replay window
        handler.Verify(Request(body, new()), "secret").Should().BeFalse();
    }

    [Fact]
    public void Twilio_signature_follows_the_documented_algorithm()
    {
        const string url = "https://gw.example.com/api/webhooks/twilio-sms/abc";
        var form = new Dictionary<string, string> { ["MessageSid"] = "SM1", ["MessageStatus"] = "delivered", ["AccountSid"] = "AC1" };
        var data = url + string.Concat(form.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value));
        var sig = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("token"), Encoding.UTF8.GetBytes(data)));

        var handler = new TwilioWebhookHandler();
        handler.Verify(Request("", new() { ["X-Twilio-Signature"] = sig }, form, url), "token").Should().BeTrue();
        handler.Verify(Request("", new() { ["X-Twilio-Signature"] = sig }, form, url + "x"), "token").Should().BeFalse();

        var receipt = handler.Parse(Request("", new(), form, url)).Single();
        receipt.Status.Should().Be(ReceiptStatus.Delivered);
        receipt.ProviderMessageId.Should().Be("SM1");
        receipt.EventId.Should().Be("SM1:delivered");
    }

    [Fact]
    public void SendGrid_signature_is_verified_with_the_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string body = """[{"sg_event_id":"e1","sg_message_id":"msg123.filter0001","event":"delivered","timestamp":1700000000}]""";
        var sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(timestamp + body), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var headers = new Dictionary<string, string>
        {
            ["X-Twilio-Email-Event-Webhook-Signature"] = sig,
            ["X-Twilio-Email-Event-Webhook-Timestamp"] = timestamp
        };
        var handler = new SendGridWebhookHandler();
        handler.Verify(Request(body, headers), publicKey).Should().BeTrue();
        handler.Verify(Request(body.Replace("delivered", "bounce"), headers), publicKey).Should().BeFalse();
        handler.Parse(Request(body, headers)).Single().ProviderMessageId.Should().Be("msg123");
    }
}

public sealed class SsrfProtectionTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.169.254", true)]   // cloud metadata
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Private_and_reserved_addresses_are_detected(string ip, bool blocked) =>
        NotificationGateway.Infrastructure.Security.IpRules.IsPrivateOrReserved(System.Net.IPAddress.Parse(ip)).Should().Be(blocked);

    [Fact]
    public async Task Provider_endpoints_pointing_inside_the_network_are_rejected()
    {
        var validator = new NotificationGateway.Infrastructure.Security.ProviderEndpointValidator(
            Microsoft.Extensions.Options.Options.Create(new NotificationGateway.Infrastructure.Security.OutboundSecurityOptions { AllowPrivateNetworks = false }));

        var errors = await validator.ValidateAsync(JsonSerializer.SerializeToElement(new
        {
            baseUrl = "https://169.254.169.254/latest/meta-data",
            statusCallbackUrl = "http://example.com/cb",
            host = "10.0.0.5",
            from = "not-an-endpoint"
        }), default);

        errors.Should().HaveCount(3);
        errors.Should().Contain(e => e.Contains("baseUrl")).And.Contain(e => e.Contains("statusCallbackUrl")).And.Contain(e => e.Contains("host"));
    }
}

public sealed class SmsEncodingTests
{
    [Fact]
    public void Latin_text_is_gsm7_and_arabic_is_ucs2()
    {
        SmsEncoding.RequiresUnicode("Ahmed late 09:42").Should().BeFalse();
        SmsEncoding.RequiresUnicode("تأخير أحمد").Should().BeTrue();
        SmsEncoding.Segments(new string('a', 160)).Should().Be(1);
        SmsEncoding.Segments(new string('a', 161)).Should().Be(2);
        SmsEncoding.Segments(new string('ت', 70)).Should().Be(1);
        SmsEncoding.Segments(new string('ت', 71)).Should().Be(2);
    }
}
