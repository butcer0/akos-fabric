using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AkosFabric.Api.Contracts;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Domain.RepositorySessions;
using AkosFabric.Domain.WorkItems;
using AkosFabric.Identity;
using Duende.IdentityModel.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AkosFabric.IntegrationTests.Security;

public sealed class RepositorySessionEndpointAuthorizationTests
    : IClassFixture<DevelopmentIdentityFixture>
{
    private static readonly string[] JiraKeys = ["KAN-1"];

    private readonly DevelopmentIdentityFixture fixture;

    public RepositorySessionEndpointAuthorizationTests(
        DevelopmentIdentityFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task SessionEndpointsReturnUnauthorizedWithoutAccessToken()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        using var client = api.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/repository-sessions/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsForbiddenWhenTokenOnlyHasReadScope()
    {
        using var identityServer = fixture.CreateIdentityServer();
        string token = await GetTokenAsync(
            identityServer,
            IdentityConfiguration.SessionsReadScope);
        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/repository-sessions",
            new
            {
                repositoryProfile = "akos-fabric",
                jiraKeys = JiraKeys,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateUsesValidatedTokenClaimsAsAuditIdentity()
    {
        using var identityServer = fixture.CreateIdentityServer();
        string token = await GetTokenAsync(
            identityServer,
            IdentityConfiguration.SessionsCreateScope);
        var service = new CapturingSessionService();
        using var api = DevelopmentIdentityFixture
            .CreateApi(identityServer)
            .WithWebHostBuilder(
                builder => builder.ConfigureServices(
                    services =>
                    {
                        services.RemoveAll<IRepositorySessionService>();
                        services.AddSingleton<IRepositorySessionService>(service);
                    }));
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/repository-sessions",
            new
            {
                repositoryProfile = "akos-fabric",
                jiraKeys = JiraKeys,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(service.Caller);
        Assert.Equal(IdentityConfiguration.DevelopmentSubject, service.Caller.Subject);
        Assert.Equal(
            IdentityConfiguration.DevelopmentClientId,
            service.Caller.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(service.Caller.TokenId));
        Assert.False(string.IsNullOrWhiteSpace(service.Caller.TraceParent));
        Assert.Equal("akos-fabric", service.Input?.RepositoryProfile);
        Assert.Equal(["KAN-1"], service.Input?.JiraKeys);
    }

    [Fact]
    public async Task CreateRejectsCallerIdentityInRequestBody()
    {
        using var identityServer = fixture.CreateIdentityServer();
        string token = await GetTokenAsync(
            identityServer,
            IdentityConfiguration.SessionsCreateScope);
        var service = new CapturingSessionService();
        using var api = DevelopmentIdentityFixture
            .CreateApi(identityServer)
            .WithWebHostBuilder(
                builder => builder.ConfigureServices(
                    services =>
                    {
                        services.RemoveAll<IRepositorySessionService>();
                        services.AddSingleton<IRepositorySessionService>(service);
                    }));
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using var content = new StringContent(
            """
            {
              "repositoryProfile": "akos-fabric",
              "jiraKeys": ["KAN-1"],
              "requestedBy": "body-supplied-identity"
            }
            """,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/repository-sessions",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(service.Caller);
    }

    [Fact]
    public async Task OperateScopeCanReprocessValidRecoveredResult()
    {
        using var identityServer = fixture.CreateIdentityServer();
        string token = await GetTokenAsync(
            identityServer,
            IdentityConfiguration.SessionsOperateScope);
        var service = new CapturingSessionService();
        var resultProcessor = new CapturingResultProcessor();
        using var api = DevelopmentIdentityFixture
            .CreateApi(identityServer)
            .WithWebHostBuilder(
                builder => builder.ConfigureServices(
                    services =>
                    {
                        services.RemoveAll<IRepositorySessionService>();
                        services.RemoveAll<IAgentResultProcessor>();
                        services.AddSingleton<IRepositorySessionService>(service);
                        services.AddSingleton<IAgentResultProcessor>(
                            resultProcessor);
                    }));
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/repository-sessions/{service.Details.Session.Id:D}" +
            "/reprocess-result",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(service.Details.Session.Id, resultProcessor.SessionId);
    }

    [Fact]
    public async Task ReadScopeCanListSessionsWithBoundedLimit()
    {
        using var identityServer = fixture.CreateIdentityServer();
        string token = await GetTokenAsync(
            identityServer,
            IdentityConfiguration.SessionsReadScope);
        var service = new CapturingSessionService();
        using var api = DevelopmentIdentityFixture
            .CreateApi(identityServer)
            .WithWebHostBuilder(
                builder => builder.ConfigureServices(
                    services =>
                    {
                        services.RemoveAll<IRepositorySessionService>();
                        services.AddSingleton<IRepositorySessionService>(service);
                    }));
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/repository-sessions?limit=17");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(17, service.ListLimit);
        RepositorySessionResponse[]? sessions =
            await response.Content.ReadFromJsonAsync<RepositorySessionResponse[]>();
        Assert.Single(Assert.IsType<RepositorySessionResponse[]>(sessions));
    }

    private static async Task<string> GetTokenAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<
            AkosFabric.Identity.IdentityAssembly> identityServer,
        string scope)
    {
        using HttpClient identityClient =
            DevelopmentIdentityFixture.CreateIdentityClient(identityServer);
        TokenResponse response =
            await identityClient.RequestClientCredentialsTokenAsync(
                new ClientCredentialsTokenRequest
                {
                    Address =
                        $"{DevelopmentIdentityFixture.Authority}/connect/token",
                    ClientId = IdentityConfiguration.DevelopmentClientId,
                    ClientSecret = DevelopmentIdentityFixture.ClientSecret,
                    Scope = scope,
                });
        Assert.False(response.IsError, response.Error);
        return Assert.IsType<string>(response.AccessToken);
    }

    private sealed class CapturingSessionService : IRepositorySessionService
    {
        public CreateRepositorySessionInput? Input { get; private set; }
        public RepositorySessionCaller? Caller { get; private set; }
        public RepositorySessionDetails Details { get; } = CreateDetails();
        public int? ListLimit { get; private set; }

        public Task<RepositorySessionDetails> CreateAsync(
            CreateRepositorySessionInput input,
            RepositorySessionCaller caller,
            CancellationToken cancellationToken)
        {
            Input = input;
            Caller = caller;
            return Task.FromResult(Details);
        }

        public Task<RepositorySessionDetails> PublishAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> RetryAsync(
            Guid repositorySessionId,
            RepositorySessionCaller caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> CancelAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RepositorySessionDetails> GetAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Details);

        public Task<IReadOnlyList<RepositorySessionRecord>> ListAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            ListLimit = limit;
            return Task.FromResult<IReadOnlyList<RepositorySessionRecord>>(
                [Details.Session]);
        }

        public Task<IReadOnlyList<WorkItemRunRecord>> ListItemsAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static RepositorySessionDetails CreateDetails()
        {
            var sessionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new RepositorySessionDetails(
                new RepositorySessionRecord(
                    sessionId,
                    "akos-fabric",
                    new string('a', 40),
                    "github",
                    "akos-fabric-agent:1.4",
                    $"sha256:{new string('b', 64)}",
                    RepositorySessionStatus.Published,
                    Guid.NewGuid(),
                    null,
                    null,
                    """{"repositoryProfile":"akos-fabric","jiraKeys":["KAN-1"]}""",
                    IdentityConfiguration.DevelopmentSubject,
                    IdentityConfiguration.DevelopmentClientId,
                    "token-id",
                    "trace-id",
                    now,
                    now,
                    null,
                    null,
                    null,
                    null),
                [
                    new WorkItemRunRecord(
                        Guid.NewGuid(),
                        sessionId,
                        1,
                        "1",
                        "KAN-1",
                        now,
                        """{"id":"1","key":"KAN-1"}""",
                        WorkItemRunStatus.Queued,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ]);
        }
    }

    private sealed class CapturingResultProcessor : IAgentResultProcessor
    {
        public Guid? SessionId { get; private set; }

        public Task ProcessAsync(
            RepositorySessionContainer container,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProcessRecoveredAsync(
            Guid repositorySessionId,
            CancellationToken cancellationToken)
        {
            SessionId = repositorySessionId;
            return Task.CompletedTask;
        }
    }
}
