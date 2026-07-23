using AkosFabric.Api.Configuration;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Infrastructure.Persistence;

namespace AkosFabric.Api.HostedServices;

internal sealed class AgentControlStartupHostedService : IHostedService
{
    private readonly AgentControlHostOptions options;
    private readonly IServiceProvider services;

    public AgentControlStartupHostedService(
        AgentControlHostOptions options,
        IServiceProvider services)
    {
        this.options =
            options ?? throw new ArgumentNullException(nameof(options));
        this.services =
            services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        if (!options.Enabled)
        {
            return;
        }

        await using AsyncServiceScope scope =
            services.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<
            AgentControlRuntimeConfigurationValidator>();

        if (options.MigrateDatabaseOnStart)
        {
            await scope.ServiceProvider
                .GetRequiredService<PostgresMigrationRunner>()
                .MigrateAsync(cancellationToken);
        }

        if (options.ReconcileDockerOnStart)
        {
            await scope.ServiceProvider
                .GetRequiredService<IRepositorySessionStartupReconciler>()
                .ReconcileOnceAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
