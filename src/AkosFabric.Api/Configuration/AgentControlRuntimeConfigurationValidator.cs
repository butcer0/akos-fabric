using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.RepositoryProfiles.Interfaces;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Infrastructure.Execution;
using AkosFabric.Infrastructure.Jira;
using AkosFabric.Infrastructure.Persistence;

using Npgsql;

namespace AkosFabric.Api.Configuration;

internal sealed class AgentControlRuntimeConfigurationValidator
{
    public AgentControlRuntimeConfigurationValidator(
        AgentControlHostOptions hostOptions,
        SourceControlHostOptions sourceControlOptions,
        JiraOptions jiraOptions,
        JiraEnvironmentCredentialBinding[] jiraCredentialBindings,
        LlmEnvironmentCredentialBinding[] llmCredentialBindings,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(sourceControlOptions);
        ArgumentNullException.ThrowIfNull(jiraOptions);
        ArgumentNullException.ThrowIfNull(jiraCredentialBindings);
        ArgumentNullException.ThrowIfNull(llmCredentialBindings);
        ArgumentNullException.ThrowIfNull(services);
        hostOptions.Validate();

        if (!hostOptions.Enabled)
        {
            return;
        }

        if (!sourceControlOptions.GitHub.Enabled)
        {
            throw new InvalidOperationException(
                "The enabled control plane requires at least one operational " +
                "source-control provider. Only the GitHub adapter is " +
                "operational in the current version.");
        }

        ValidateJira(jiraOptions, jiraCredentialBindings);
        if (llmCredentialBindings.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one Llm:CredentialEnvironmentVariables binding is " +
                "required when AgentControl is enabled.");
        }

        // Resolving the complete graph validates paths, schemas, endpoint
        // shapes, provider bindings, and local key material. These
        // constructors do not perform network, database, broker, or Docker
        // calls.
        _ = services.GetRequiredService<NpgsqlDataSource>();
        _ = services.GetRequiredService<IRepositoryProfileProvider>();
        _ = services.GetRequiredService<IJiraClient>();
        _ = services.GetRequiredService<IRepositorySessionQueue>();
        _ = services.GetRequiredService<IRepositorySessionRepository>();
        _ = services.GetRequiredService<ISourceControlProviderResolver>();
        _ = services.GetRequiredService<
            ISourceControlCredentialProviderResolver>();
        _ = services.GetRequiredService<
            ISourceControlCredentialAcquisitionService>();
        _ = services.GetRequiredService<ILlmApiCredentialProvider>();
        _ = services.GetRequiredService<IRepositorySessionExecutor>();
        _ = services.GetRequiredService<
            IRepositorySessionExecutionRequestFactory>();
        _ = services.GetRequiredService<IAgentSessionArtifactReader>();
        _ = services.GetRequiredService<IAgentResultProcessor>();
        _ = services.GetRequiredService<IRepositorySessionMonitorAttacher>();
        _ = services.GetRequiredService<
            IRepositorySessionStartupReconciler>();
        _ = services.GetRequiredService<
            ISessionArtifactRetentionScheduler>();
        _ = services.GetRequiredService<IRepositorySessionService>();
    }

    private static void ValidateJira(
        JiraOptions options,
        JiraEnvironmentCredentialBinding[] credentialBindings)
    {
        if (options.Sites.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one Jira site is required when AgentControl is enabled.");
        }

        var profiles = credentialBindings
            .Select(binding => binding.AuthenticationProfile)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string siteName, JiraSiteOptions site) in options.Sites)
        {
            if (!site.BaseUrl.IsAbsoluteUri ||
                !string.Equals(
                    site.BaseUrl.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(site.BaseUrl.UserInfo) ||
                !string.IsNullOrEmpty(site.BaseUrl.Query) ||
                !string.IsNullOrEmpty(site.BaseUrl.Fragment))
            {
                throw new InvalidOperationException(
                    $"Jira site '{siteName}' must use an absolute HTTPS base " +
                    "URL without user information, query, or fragment.");
            }

            if (string.IsNullOrWhiteSpace(site.AuthenticationProfile) ||
                !profiles.Contains(site.AuthenticationProfile))
            {
                throw new InvalidOperationException(
                    $"Jira site '{siteName}' does not have a matching " +
                    "credential environment-variable binding.");
            }
        }
    }
}
