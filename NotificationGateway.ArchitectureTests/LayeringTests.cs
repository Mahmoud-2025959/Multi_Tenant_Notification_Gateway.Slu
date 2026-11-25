using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using NotificationGateway.Application;
using NotificationGateway.Domain.Common;
using Xunit;
namespace NotificationGateway.ArchitectureTests
{
    /// <summary>Clean Architecture dependency rules + "business code never touches provider SDKs".</summary>
    public sealed class LayeringTests
    {
        private static readonly Assembly Domain = typeof(Entity).Assembly;
        private static readonly Assembly Application = typeof(DependencyInjection).Assembly;
        private static readonly Assembly Infrastructure = typeof(NotificationGateway.Infrastructure.DependencyInjection).Assembly;

        [Fact]
        public void Domain_depends_on_nothing()
        {
            var result = Types.InAssembly(Domain).ShouldNot()
                .HaveDependencyOnAny("NotificationGateway.Application", "NotificationGateway.Infrastructure", "NotificationGateway.Api",
                    "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? new List<string>()));
        }

        [Fact]
        public void Application_does_not_depend_on_Infrastructure_or_Api()
        {
            var result = Types.InAssembly(Application).ShouldNot()
                .HaveDependencyOnAny("NotificationGateway.Infrastructure", "NotificationGateway.Api", "Microsoft.AspNetCore")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? new List<string>()));
        }

        [Fact]
        public void Only_provider_adapters_reference_provider_SDKs()
        {
            var result = Types.InAssembly(Infrastructure)
                .That().DoNotResideInNamespaceStartingWith("NotificationGateway.Infrastructure.Providers")
                .And().DoNotResideInNamespaceStartingWith("NotificationGateway.Infrastructure.Messaging")
                .ShouldNot().HaveDependencyOnAny("MailKit", "MimeKit", "RabbitMQ.Client")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? new List<string>()));
        }

        [Fact]
        public void Application_and_Domain_never_reference_provider_SDKs()
        {
            var result = Types.InAssemblies(new[] { Domain, Application }).ShouldNot()
                .HaveDependencyOnAny("MailKit", "MimeKit", "RabbitMQ.Client")
                .GetResult();

            result.IsSuccessful.Should().BeTrue();
        }
    }
}
