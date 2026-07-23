using System.Net;
using AkosFabric.Api;
using AkosFabric.Api.Configuration;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.Telemetry;
using AkosFabric.Infrastructure.Execution;
using AkosFabric.Infrastructure.Jira;
using AkosFabric.Infrastructure.Messaging;
using AkosFabric.Infrastructure.RepositoryProfiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AkosFabric.IntegrationTests.Hosting;

public sealed class AgentControlHostCompositionTests
{
    [Fact]
    public async Task DefaultHostStartsWithoutExternalDependencies()
    {
        await using WebApplicationFactory<ApiAssembly> application =
            CreateApplication();
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AgentControlHostOptions options =
            application.Services.GetRequiredService<
                AgentControlHostOptions>();
        Assert.False(options.Enabled);
        RepositoryProfileOptions profileOptions =
            application.Services.GetRequiredService<
                RepositoryProfileOptions>();
        AgentSessionArtifactReaderOptions artifactOptions =
            application.Services.GetRequiredService<
                AgentSessionArtifactReaderOptions>();
        RepositorySessionMonitorOptions monitorOptions =
            application.Services.GetRequiredService<
                RepositorySessionMonitorOptions>();
        Assert.True(Directory.Exists(profileOptions.RootPath));
        Assert.True(File.Exists(profileOptions.SchemaPath));
        Assert.True(File.Exists(artifactOptions.ManifestSchemaPath));
        Assert.True(File.Exists(artifactOptions.ResultSchemaPath));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            monitorOptions.CredentialRefreshSafetyMargin);

        IHostedService[] hostedServices =
            application.Services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(
            hostedServices,
            service => service is RabbitMqRepositorySessionConsumer);
        Assert.DoesNotContain(
            hostedServices,
            service => service is JiraSelectionWorker);
        Assert.DoesNotContain(
            hostedServices,
            service =>
                service.GetType().Name ==
                "SessionArtifactRetentionHostedService");
    }

    [Fact]
    public async Task AnonymousLivenessDoesNotExecuteOrDiscloseReadinessChecks()
    {
        await using WebApplicationFactory<ApiAssembly> application =
            CreateApplication()
                .WithWebHostBuilder(
                    builder => builder.ConfigureServices(
                        services => services
                            .AddHealthChecks()
                            .AddCheck(
                                "dependency-sentinel",
                                () => HealthCheckResult.Unhealthy(
                                    "must not be disclosed"),
                                tags: ["ready"])));
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync("/health/live");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "dependency-sentinel",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must not be disclosed",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultHostRegistersControlPlaneSeamsLazily()
    {
        using WebApplicationFactory<ApiAssembly> application =
            CreateApplication();
        _ = application.Services;
        IServiceProviderIsService serviceProbe =
            application.Services.GetRequiredService<
                IServiceProviderIsService>();

        Type[] registeredSeams =
        [
            typeof(IRepositorySessionService),
            typeof(IRepositorySessionRepository),
            typeof(IRepositorySessionQueue),
            typeof(IRepositoryProfileProvider),
            typeof(IJiraClient),
            typeof(IJiraSelectionService),
            typeof(ISourceControlProviderResolver),
            typeof(ISourceControlCredentialProviderResolver),
            typeof(ISourceControlCredentialAcquisitionService),
            typeof(ILlmApiCredentialProvider),
            typeof(IRepositorySessionExecutor),
            typeof(IRepositorySessionExecutionRequestFactory),
            typeof(IAgentSessionArtifactReader),
            typeof(IAgentResultProcessor),
            typeof(IRepositorySessionMonitorAttacher),
            typeof(IRepositorySessionStartupReconciler),
            typeof(ISessionArtifactRetentionScheduler),
            typeof(IAgentControlMetrics),
            typeof(IAgentControlLifecycleLogger),
        ];

        Assert.All(
            registeredSeams,
            serviceType => Assert.True(
                serviceProbe.IsService(serviceType),
                $"{serviceType.FullName} is not registered."));
    }

    [Fact]
    public void ExternalFeatureCannotBeEnabledWhileControlPlaneIsDisabled()
    {
        var options = new AgentControlHostOptions
        {
            Enabled = false,
            StartRabbitMqConsumer = true,
        };

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(
            "AgentControl must be enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionCleanupIntervalMustBePositive()
    {
        var options = new AgentControlHostOptions
        {
            RetentionCleanupInterval = TimeSpan.Zero,
        };

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(
            "RetentionCleanupInterval",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledHostFailsBeforeExternalCallsWhenProviderIsUnconfigured()
    {
        using WebApplicationFactory<ApiAssembly> application =
            new WebApplicationFactory<ApiAssembly>()
                .WithWebHostBuilder(
                    builder =>
                    {
                        builder.ConfigureLogging(
                            logging => logging.ClearProviders());
                        builder.UseEnvironment("Development");
                        builder.UseSetting(
                            "AgentControl:Enabled",
                            "true");
                    });

        Exception exception = Assert.ThrowsAny<Exception>(
            () => _ = application.Services);

        Assert.Contains(
            "at least one operational source-control provider",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationFactory<ApiAssembly> CreateApplication() =>
        new WebApplicationFactory<ApiAssembly>()
            .WithWebHostBuilder(
                builder =>
                {
                    builder.ConfigureLogging(
                        logging => logging.ClearProviders());
                    builder.UseEnvironment("Development");
                });
}
