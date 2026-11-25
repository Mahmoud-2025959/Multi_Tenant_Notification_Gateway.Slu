using FluentAssertions;
using NotificationGateway.Domain.Enums;
using NotificationGateway.Domain.Preferences;
using NotificationGateway.Domain.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NotificationGateway.UnitTests.Domain
{
    public sealed class UserPreferenceTests
    {
        [Theory]
        [InlineData("23:00", true)]
        [InlineData("02:00", true)]
        [InlineData("06:59", true)]
        [InlineData("07:00", false)]
        [InlineData("12:00", false)]
        public void Quiet_hours_crossing_midnight(string time, bool inside)
        {
            var p = new NotificationUserPreference(Guid.NewGuid(), "u1");
            p.Update(null, Array.Empty<NotificationChannel>(), Array.Empty<string>(),
                new TimeOnly(22, 0), new TimeOnly(7, 0), "Africa/Cairo", true, DateTimeOffset.UtcNow);

            (p.QuietWindowEndIfInside(TimeOnly.Parse(time)) is not null).Should().Be(inside);
        }

        [Fact]
        public void Disabled_channels_round_trip()
        {
            var p = new NotificationUserPreference(Guid.NewGuid(), "u1");
            p.Update(TemplateLanguage.Ar, new[] { NotificationChannel.Email, NotificationChannel.Sms }, new[] { "Marketing" },
                null, null, null, true, DateTimeOffset.UtcNow);

            p.DisabledChannelSet.Should().BeEquivalentTo(new[] { NotificationChannel.Email, NotificationChannel.Sms });
            p.DisabledTypeSet.Contains("marketing").Should().BeTrue("type opt-outs are case-insensitive");
        }

        [Fact]
        public void Template_extracts_placeholders_from_subject_and_body()
        {
            var names = NotificationTemplate.ExtractPlaceholders("Hi {{EmployeeName}}", "Recorded at {{ CheckInTime }} by {{EmployeeName}}");
            names.Should().BeEquivalentTo(new[] { "EmployeeName", "CheckInTime" });
        }
    }
}
