using FluentAssertions;
using Microsoft.Extensions.Options;
using NotificationGateway.Infrastructure.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NotificationGateway.UnitTests.Security
{
    public sealed class AesGcmSecretProtectorTests
    {
        private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        private static AesGcmSecretProtector Create(string activeKeyId, Dictionary<string, string> keys) =>
            new(Options.Create(new SecretProtectionOptions { ActiveKeyId = activeKeyId, Keys = keys }));

        [Fact]
        public void Round_trips_and_does_not_contain_plaintext()
        {
            var sut = Create("k1", new() { ["k1"] = NewKey() });

            var envelope = sut.Protect("SG.super-secret-api-key", "provider-config:a");

            envelope.Should().StartWith("v1:k1:");
            envelope.Should().NotContain("super-secret");
            sut.Unprotect(envelope, "provider-config:a").Should().Be("SG.super-secret-api-key");
        }

        [Fact]
        public void Envelope_copied_to_another_configuration_cannot_be_decrypted()
        {
            var sut = Create("k1", new() { ["k1"] = NewKey() });
            var envelope = sut.Protect("tenant-A-secret", "provider-config:tenantA-config");

            var act = () => sut.Unprotect(envelope, "provider-config:tenantB-config");

            act.Should().Throw<CryptographicException>();
        }

        [Fact]
        public void Tampered_envelope_is_rejected()
        {
            var sut = Create("k1", new() { ["k1"] = NewKey() });
            var envelope = sut.Protect("secret", "b");
            var parts = envelope.Split(':');
            var bytes = Convert.FromBase64String(parts[2]);
            bytes[^1] ^= 0xFF;
            var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(bytes)}";

            var act = () => sut.Unprotect(tampered, "b");

            act.Should().Throw<CryptographicException>();
        }

        [Fact]
        public void Master_key_rotation_keeps_old_envelopes_readable_and_flags_them()
        {
            var k1 = NewKey();
            var old = Create("k1", new() { ["k1"] = k1 }).Protect("secret", "b");

            var rotated = Create("k2", new() { ["k1"] = k1, ["k2"] = NewKey() });

            rotated.Unprotect(old, "b").Should().Be("secret");
            rotated.NeedsReEncryption(old).Should().BeTrue();
            rotated.NeedsReEncryption(rotated.Protect("secret", "b")).Should().BeFalse();
        }

        [Fact]
        public void Rejects_keys_that_are_not_256_bit()
        {
            var act = () => Create("k1", new() { ["k1"] = Convert.ToBase64String(new byte[16]) });
            act.Should().Throw<InvalidOperationException>();
        }
    }
}
