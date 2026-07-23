using AkosFabric.Api.Configuration;
using AkosFabric.Api.Health;
using AkosFabric.Api.HostedServices;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Services;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Services;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Services;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Services;
using AkosFabric.Application.Telemetry;
using AkosFabric.Infrastructure.Execution;
using AkosFabric.Infrastructure.Jira;
using AkosFabric.Infrastructure.Messaging;
using AkosFabric.Infrastructure.Persistence;
using AkosFabric.Infrastructure.RepositoryProfiles;
using AkosFabric.Infrastructure.SourceControl;
using AkosFabric.Infrastructure.SourceControl.GitHub;
using AkosFabric.Infrastructure.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AkosFabric.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentControl(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        AgentControlHostOptions hostOptions =
            configuration
                .GetSection(AgentControlHostOptions.SectionName)
                .Get<AgentControlHostOptions>()
            ?? new AgentControlHostOptions();
        hostOptions.Validate();

        RepositoryProfileOptions profileOptions =
            ReadProfileOptions(configuration, environment.ContentRootPath);
        JiraOptions jiraOptions = ReadJiraOptions(configuration);
        JiraSelectionOptions selectionOptions =
            configuration.GetSection("Jira:Selection")
                .Get<JiraSelectionOptions>()
            ?? new JiraSelectionOptions();
        RabbitMqOptions rabbitOptions = ReadRabbitOptions(configuration);
        DockerExecutionOptions dockerOptions =
            configuration.GetSection("Execution:Docker")
                .Get<DockerExecutionOptions>()
            ?? new DockerExecutionOptions();
        SessionFileStoreOptions fileStoreOptions =
            ReadSessionFileStoreOptions(
                configuration,
                environment.ContentRootPath);
        AgentSessionArtifactReaderOptions artifactOptions =
            ReadArtifactOptions(configuration, environment.ContentRootPath);
        RepositorySessionExecutionRequestFactoryOptions requestOptions =
            configuration.GetSection("Execution:Request")
                .Get<RepositorySessionExecutionRequestFactoryOptions>()
            ?? new RepositorySessionExecutionRequestFactoryOptions();
        RepositorySessionMonitorOptions monitorOptions =
            configuration.GetSection("Execution:Monitor")
                .Get<RepositorySessionMonitorOptions>()
            ?? new RepositorySessionMonitorOptions();
        SessionArtifactRetentionOptions retentionOptions =
            configuration.GetSection("Execution:Retention")
                .Get<SessionArtifactRetentionOptions>()
            ?? new SessionArtifactRetentionOptions();
        SourceControlHostOptions sourceControlOptions =
            ReadSourceControlOptions(
                configuration,
                environment.ContentRootPath);
        JiraEnvironmentCredentialBinding[] jiraCredentialBindings =
            ReadJiraCredentialBindings(configuration);
        LlmEnvironmentCredentialBinding[] llmCredentialBindings =
            ReadLlmCredentialBindings(configuration);

        services.AddSingleton(hostOptions);
        services.AddSingleton(profileOptions);
        services.AddSingleton(jiraOptions);
        services.AddSingleton(selectionOptions);
        services.AddSingleton(rabbitOptions);
        services.AddSingleton(dockerOptions);
        services.AddSingleton(fileStoreOptions);
        services.AddSingleton(artifactOptions);
        services.AddSingleton(requestOptions);
        services.AddSingleton(monitorOptions);
        services.AddSingleton(retentionOptions);
        services.AddSingleton(sourceControlOptions);
        services.AddSingleton(jiraCredentialBindings);
        services.AddSingleton(llmCredentialBindings);
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient("jira");
        services.AddHttpClient("github");

        services.AddSingleton(
            _ => CreatePostgresDataSource(configuration));
        services.AddSingleton<PostgresMigrationRunner>();
        services.AddSingleton<PostgresRepositorySessionRepository>();
        services.AddSingleton<IRepositorySessionRepository>(
            provider => provider.GetRequiredService<
                PostgresRepositorySessionRepository>());
        services.AddSingleton<IJiraSelectionRepository>(
            provider => provider.GetRequiredService<
                PostgresRepositorySessionRepository>());

        services.AddSingleton<IRepositoryProfileProvider,
            FileRepositoryProfileProvider>();
        services.AddSingleton<IJiraAccessTokenProvider>(
            _ => new EnvironmentJiraAccessTokenProvider(
                jiraCredentialBindings));
        services.AddSingleton<IJiraClient>(
            provider => new JiraClient(
                provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("jira"),
                provider.GetRequiredService<IJiraAccessTokenProvider>(),
                provider.GetRequiredService<JiraOptions>()));
        services.AddSingleton<IRepositorySessionQueue,
            RabbitMqRepositorySessionQueue>();

        services.AddSingleton<ILlmApiCredentialProvider>(
            _ => new EnvironmentLlmApiCredentialProvider(
                llmCredentialBindings));
        AddSourceControl(
            services,
            sourceControlOptions);
        services.AddSingleton<
            ISourceControlCredentialAcquisitionService,
            SourceControlCredentialAcquisitionService>();

        services.AddSingleton<SessionFileStore>();
        services.AddSingleton<DockerContainerClient>();
        services.AddSingleton<IRepositorySessionExecutor,
            DockerRepositorySessionExecutor>();
        services.AddSingleton<IRepositorySessionExecutionRequestFactory,
            RepositorySessionExecutionRequestFactory>();
        services.AddSingleton<IAgentSessionArtifactReader,
            FileAgentSessionArtifactReader>();
        services.AddSingleton<IAgentResultProcessor, AgentResultProcessor>();
        services.AddSingleton<SessionArtifactRetentionCleaner>();
        services.AddSingleton<ISessionArtifactRetentionScheduler>(
            provider => provider.GetRequiredService<
                SessionArtifactRetentionCleaner>());
        services.AddSingleton<ContainerCompletionMonitor>(
            provider => new ContainerCompletionMonitor(
                provider.GetRequiredService<IRepositorySessionExecutor>(),
                provider.GetRequiredService<IRepositorySessionRepository>(),
                provider.GetRequiredService<IRepositoryProfileProvider>(),
                provider.GetRequiredService<IAgentResultProcessor>(),
                provider.GetRequiredService<
                    ISessionArtifactRetentionScheduler>(),
                provider.GetRequiredService<
                    ISourceControlCredentialAcquisitionService>(),
                provider.GetRequiredService<
                    RepositorySessionMonitorOptions>(),
                provider.GetRequiredService<IAgentControlMetrics>(),
                provider.GetRequiredService<
                    IAgentControlLifecycleLogger>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStopping));
        services.AddSingleton<IRepositorySessionMonitorAttacher>(
            provider => provider.GetRequiredService<
                ContainerCompletionMonitor>());
        services.AddSingleton<IRepositorySessionStartupReconciler,
            RepositorySessionStartupReconciler>();
        services.AddSingleton<RepositorySessionDeliveryHandler>();
        services.AddSingleton<IRepositorySessionService,
            RepositorySessionService>();
        services.AddSingleton<IJiraSelectionService, JiraSelectionService>();

        services.AddSingleton<AgentControlRuntimeConfigurationValidator>();
        services.AddHostedService<AgentControlStartupHostedService>();
        if (hostOptions.Enabled && hostOptions.StartRabbitMqConsumer)
        {
            services.AddHostedService<RabbitMqRepositorySessionConsumer>();
        }

        if (hostOptions.Enabled && hostOptions.StartJiraSelectionWorker)
        {
            services.AddHostedService<JiraSelectionWorker>();
        }

        if (hostOptions.Enabled &&
            hostOptions.StartRetentionCleanupWorker)
        {
            services.AddHostedService<
                SessionArtifactRetentionHostedService>();
        }

        if (hostOptions.Enabled && hostOptions.ExportTelemetry)
        {
            AkosControlTelemetryOptions telemetryOptions =
                configuration.GetSection("Telemetry")
                    .Get<AkosControlTelemetryOptions>()
                ?? new AkosControlTelemetryOptions();
            services.AddAkosControlTelemetry(telemetryOptions);
        }
        else
        {
            services.AddSingleton<AgentControlMetrics>();
        }
        services.AddSingleton<IAgentControlMetrics>(
            provider => provider.GetRequiredService<AgentControlMetrics>());
        services.AddSingleton<IAgentControlLifecycleLogger,
            AgentControlLifecycleLogger>();
        IHealthChecksBuilder healthChecks = services.AddHealthChecks();
        if (hostOptions.Enabled)
        {
            healthChecks
                .AddCheck<PostgresReadinessHealthCheck>(
                    "postgres",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: ["ready"],
                    timeout: TimeSpan.FromSeconds(5))
                .AddCheck<RabbitMqReadinessHealthCheck>(
                    "rabbitmq",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: ["ready"],
                    timeout: TimeSpan.FromSeconds(5));
        }

        return services;
    }

    private static void AddSourceControl(
        IServiceCollection services,
        SourceControlHostOptions options)
    {
        if (options.GitHub.Enabled)
        {
            var githubOptions = new GitHubOptions
            {
                ApiBaseUrl = options.GitHub.ApiBaseUrl,
                AppId = options.GitHub.AppId,
                InstallationId = options.GitHub.InstallationId,
                PrivateKeyPath = options.GitHub.PrivateKeyPath,
                UserAgent = options.GitHub.UserAgent,
            };
            services.AddSingleton(githubOptions);
            services.AddSingleton<GitHubAppCredentialProvider>(
                provider => new GitHubAppCredentialProvider(
                    provider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("github"),
                    githubOptions,
                    provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ISourceControlProvider>(
                provider => new GitHubSourceControlProvider(
                    provider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("github"),
                    provider.GetRequiredService<
                        GitHubAppCredentialProvider>(),
                    githubOptions));
            services.AddSingleton(
                provider =>
                    new SourceControlCredentialProviderRegistration(
                        "github",
                        options.GitHub.AuthenticationProfile,
                        provider.GetRequiredService<
                            GitHubAppCredentialProvider>()));
        }

        services.AddSingleton<ISourceControlProviderResolver,
            SourceControlProviderResolver>();
        services.AddSingleton<ISourceControlCredentialProviderResolver,
            SourceControlCredentialProviderResolver>();
    }

    private static NpgsqlDataSource CreatePostgresDataSource(
        IConfiguration configuration)
    {
        string connectionString =
            configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is required when AgentControl is enabled.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is required when AgentControl is enabled.");
        }

        try
        {
            return NpgsqlDataSource.Create(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is invalid.",
                exception);
        }
    }

    private static RepositoryProfileOptions ReadProfileOptions(
        IConfiguration configuration,
        string contentRoot)
    {
        IConfigurationSection section =
            configuration.GetSection(
                RepositoryProfileOptions.SectionName);
        return new RepositoryProfileOptions
        {
            RootPath = ResolvePath(
                section[nameof(RepositoryProfileOptions.RootPath)]
                    ?? "../../repository-profiles",
                contentRoot),
            SchemaPath = ResolvePath(
                section[nameof(RepositoryProfileOptions.SchemaPath)]
                    ?? "../../schemas/repository-profile-v1.schema.json",
                contentRoot),
        };
    }

    private static JiraOptions ReadJiraOptions(
        IConfiguration configuration)
    {
        var sites = new Dictionary<string, JiraSiteOptions>(
            StringComparer.Ordinal);
        foreach (IConfigurationSection site in
                 configuration.GetSection("Jira:Sites").GetChildren())
        {
            string baseUrl = site[nameof(JiraSiteOptions.BaseUrl)]
                ?? throw new InvalidOperationException(
                    $"Jira:Sites:{site.Key}:BaseUrl is required.");
            string authenticationProfile =
                site[nameof(JiraSiteOptions.AuthenticationProfile)]
                ?? throw new InvalidOperationException(
                    $"Jira:Sites:{site.Key}:AuthenticationProfile is required.");
            if (!Uri.TryCreate(
                    baseUrl,
                    UriKind.Absolute,
                    out Uri? baseUri))
            {
                throw new InvalidOperationException(
                    $"Jira:Sites:{site.Key}:BaseUrl must be an absolute URI.");
            }

            sites.Add(
                site.Key,
                new JiraSiteOptions(
                    baseUri,
                    authenticationProfile));
        }

        return new JiraOptions(sites);
    }

    private static RabbitMqOptions ReadRabbitOptions(
        IConfiguration configuration)
    {
        IConfigurationSection section =
            configuration.GetSection("RabbitMq");
        string connectionUri =
            section[nameof(RabbitMqOptions.ConnectionUri)]
            ?? "amqp://127.0.0.1:5672";
        if (!Uri.TryCreate(
                connectionUri,
                UriKind.Absolute,
                out Uri? parsedUri))
        {
            throw new InvalidOperationException(
                "RabbitMq:ConnectionUri must be an absolute URI.");
        }

        return new RabbitMqOptions
        {
            ConnectionUri = parsedUri,
            Exchange =
                section[nameof(RabbitMqOptions.Exchange)]
                ?? RabbitMqOptions.DefaultExchange,
            Queue =
                section[nameof(RabbitMqOptions.Queue)]
                ?? RabbitMqOptions.DefaultQueue,
            RoutingKey =
                section[nameof(RabbitMqOptions.RoutingKey)]
                ?? RabbitMqOptions.DefaultRoutingKey,
            ConfirmationTimeout =
                section.GetValue<TimeSpan?>(
                    nameof(RabbitMqOptions.ConfirmationTimeout))
                ?? TimeSpan.FromSeconds(10),
        };
    }

    private static SessionFileStoreOptions ReadSessionFileStoreOptions(
        IConfiguration configuration,
        string contentRoot)
    {
        IConfigurationSection section =
            configuration.GetSection("Execution:SessionFiles");
        return new SessionFileStoreOptions
        {
            RootDirectory = ResolvePath(
                section[nameof(SessionFileStoreOptions.RootDirectory)]
                    ?? "../../.data/sessions",
                contentRoot),
            OwnerUserId =
                section.GetValue<int?>(
                    nameof(SessionFileStoreOptions.OwnerUserId))
                ?? 10001,
            OwnerGroupId =
                section.GetValue<int?>(
                    nameof(SessionFileStoreOptions.OwnerGroupId))
                ?? 10001,
        };
    }

    private static AgentSessionArtifactReaderOptions ReadArtifactOptions(
        IConfiguration configuration,
        string contentRoot)
    {
        IConfigurationSection section =
            configuration.GetSection("Execution:Artifacts");
        return new AgentSessionArtifactReaderOptions
        {
            ManifestSchemaPath = ResolvePath(
                section[
                    nameof(
                        AgentSessionArtifactReaderOptions.ManifestSchemaPath)]
                    ?? "../../schemas/agent-session-manifest-v1.schema.json",
                contentRoot),
            ResultSchemaPath = ResolvePath(
                section[
                    nameof(
                        AgentSessionArtifactReaderOptions.ResultSchemaPath)]
                    ?? "../../schemas/agent-session-result-v1.schema.json",
                contentRoot),
        };
    }

    private static SourceControlHostOptions ReadSourceControlOptions(
        IConfiguration configuration,
        string contentRoot)
    {
        GitHubHostOptions configured =
            configuration.GetSection("SourceControl:GitHub")
                .Get<GitHubHostOptions>()
            ?? new GitHubHostOptions();
        return new SourceControlHostOptions
        {
            GitHub = new GitHubHostOptions
            {
                Enabled = configured.Enabled,
                AuthenticationProfile =
                    configured.AuthenticationProfile,
                ApiBaseUrl = configured.ApiBaseUrl,
                AppId = configured.AppId,
                InstallationId = configured.InstallationId,
                PrivateKeyPath =
                    string.IsNullOrWhiteSpace(configured.PrivateKeyPath)
                        ? string.Empty
                        : ResolvePath(
                            configured.PrivateKeyPath,
                            contentRoot),
                UserAgent = configured.UserAgent,
            },
        };
    }

    private static JiraEnvironmentCredentialBinding[]
        ReadJiraCredentialBindings(IConfiguration configuration) =>
        configuration
            .GetSection("Jira:CredentialEnvironmentVariables")
            .GetChildren()
            .Select(binding =>
                new JiraEnvironmentCredentialBinding(
                    binding.Key,
                    binding.Value ?? string.Empty))
            .ToArray();

    private static LlmEnvironmentCredentialBinding[]
        ReadLlmCredentialBindings(IConfiguration configuration) =>
        configuration
            .GetSection("Llm:CredentialEnvironmentVariables")
            .GetChildren()
            .SelectMany(provider =>
                provider.GetChildren().Select(profile =>
                    new LlmEnvironmentCredentialBinding(
                        provider.Key,
                        profile.Key,
                        profile.Value ?? string.Empty)))
            .ToArray();

    private static string ResolvePath(
        string configuredPath,
        string contentRoot) =>
        Path.GetFullPath(
            Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.Combine(contentRoot, configuredPath));
}
