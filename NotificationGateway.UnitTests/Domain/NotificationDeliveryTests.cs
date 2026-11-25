using FluentAssertions;
using NotificationGateway.Domain.Common;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NotificationGateway.UnitTests.Domain
{
    public sealed class NotificationDeliveryTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        private static readonly Guid TenantId = Guid.NewGuid();

        private static NotificationDelivery NewDelivery(NotificationGateway.Domain.Enums.NotificationChannel channel = NotificationChannel.Email, int maxAttempts = 3)
        {
            var n = new Notification(TenantId, "key-1", "AttendanceCheckIn", "ATTENDANCE_CHECK_IN", NotificationPriority.High, "{}", null, null, Now);
            var r = n.AddRecipient("123", "user@example.com", "+201000000000", null, TemplateLanguage.Ar);
            return n.AddDelivery(r, channel, maxAttempts, Now);
        }

        [Fact]
        public void New_delivery_is_queued_and_due_now()
        {
            var d = NewDelivery();
            d.Status.Should().Be(DeliveryStatus.Queued);
            d.NextAttemptAt.Should().Be(Now);
            d.TenantId.Should().Be(TenantId);
        }

        [Fact]
        public void Provider_acceptance_means_Sent_not_Delivered()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));
            d.MarkCallInFlight(Guid.NewGuid(), "smtp", Now);

            d.RecordAccepted("msg-1", synchronousDelivery: false, Now);

            d.Status.Should().Be(DeliveryStatus.Sent);
            d.DeliveredAt.Should().BeNull();
            d.LockedBy.Should().BeNull();
            d.InFlightProviderConfigurationId.Should().BeNull();
        }

        [Fact]
        public void Synchronous_channel_is_Delivered_on_acceptance()
        {
            var d = NewDelivery(NotificationChannel.InApp);
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));
            d.MarkCallInFlight(Guid.NewGuid(), "inapp", Now);

            d.RecordAccepted(null, synchronousDelivery: true, Now);

            d.Status.Should().Be(DeliveryStatus.Delivered);
            d.DeliveredAt.Should().Be(Now);
        }

        [Fact]
        public void Receipt_moves_Sent_to_Delivered_and_is_idempotent()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));
            d.MarkCallInFlight(Guid.NewGuid(), "sendgrid", Now);
            d.RecordAccepted("msg-1", false, Now);

            d.ConfirmDelivered(Now.AddSeconds(5), Now.AddSeconds(6)).Should().BeTrue();
            d.ConfirmDelivered(Now.AddSeconds(5), Now.AddSeconds(7)).Should().BeFalse();
            d.Status.Should().Be(DeliveryStatus.Delivered);
        }

        [Fact]
        public void Late_failure_receipt_never_moves_Delivered_backwards()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));
            d.MarkCallInFlight(Guid.NewGuid(), "sendgrid", Now);
            d.RecordAccepted("msg-1", false, Now);
            d.ConfirmDelivered(Now, Now);

            d.ConfirmFailedByProvider("bounced", Now).Should().BeFalse();
            d.Status.Should().Be(DeliveryStatus.Delivered);
        }

        [Fact]
        public void Cannot_lease_a_delivery_that_is_already_processing()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));

            var act = () => d.StartProcessing("w2", Now, TimeSpan.FromMinutes(1));

            act.Should().Throw<DomainException>();
            d.LockedBy.Should().Be("w1");
        }

        [Fact]
        public void Expired_lease_without_call_in_flight_is_safe_to_retry()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromSeconds(30));

            d.RecoverExpiredLease(inFlightProviderSupportsIdempotency: false, Now.AddMinutes(1));

            d.Status.Should().Be(DeliveryStatus.Retrying);
            d.NextAttemptAt.Should().Be(Now.AddMinutes(1));
        }

        [Fact]
        public void Expired_lease_with_call_in_flight_on_non_idempotent_provider_becomes_Uncertain()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromSeconds(30));
            d.MarkCallInFlight(Guid.NewGuid(), "smtp", Now);

            d.RecoverExpiredLease(inFlightProviderSupportsIdempotency: false, Now.AddMinutes(1));

            d.Status.Should().Be(DeliveryStatus.Uncertain);
        }

        [Fact]
        public void Expired_lease_with_call_in_flight_on_idempotent_provider_is_retried()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromSeconds(30));
            d.MarkCallInFlight(Guid.NewGuid(), "sendgrid", Now);

            d.RecoverExpiredLease(inFlightProviderSupportsIdempotency: true, Now.AddMinutes(1));

            d.Status.Should().Be(DeliveryStatus.Retrying);
        }

        [Fact]
        public void Defer_does_not_consume_an_attempt()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));

            d.Defer(Now.AddHours(8), "quiet hours", Now);

            d.Status.Should().Be(DeliveryStatus.Queued);
            d.AttemptCount.Should().Be(0);
            d.NextAttemptAt.Should().Be(Now.AddHours(8));
        }

        [Fact]
        public void Terminal_delivery_cannot_be_cancelled()
        {
            var d = NewDelivery();
            d.StartProcessing("w1", Now, TimeSpan.FromMinutes(1));
            d.MarkCallInFlight(Guid.NewGuid(), "smtp", Now);
            d.RecordAccepted("m", false, Now);

            d.TryCancel(Now).Should().BeFalse();
        }

        [Fact]
        public void Same_channel_cannot_be_added_twice_for_one_recipient()
        {
            var n = new Notification(TenantId, null, "T", "C", NotificationPriority.Normal, "{}", null, null, Now);
            var r = n.AddRecipient("1", null, null, null, null);
            n.AddDelivery(r, NotificationChannel.InApp, 3, Now);

            var act = () => n.AddDelivery(r, NotificationChannel.InApp, 3, Now);

            act.Should().Throw<DomainException>();
        }
    }
}
