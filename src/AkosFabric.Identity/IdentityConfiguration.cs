using System.Security.Claims;
using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace AkosFabric.Identity;

public static class IdentityConfiguration
{
    public const string ApiAudience = "agent-control-api";
    public const string DevelopmentClientId = "agent-control-development-operator";
    public const string DevelopmentSubject = "service:agent-control-development-operator";
    public const string SessionsReadScope = "agent.sessions.read";
    public const string SessionsCreateScope = "agent.sessions.create";
    public const string SessionsOperateScope = "agent.sessions.operate";

    public static IReadOnlyCollection<ApiScope> ApiScopes { get; } =
    [
        new(SessionsReadScope, "Read repository sessions and work items"),
        new(SessionsCreateScope, "Create repository sessions"),
        new(SessionsOperateScope, "Publish, retry, and cancel repository sessions"),
    ];

    public static IReadOnlyCollection<ApiResource> ApiResources { get; } =
    [
        new(ApiAudience, "Akos Fabric Agent Control API")
        {
            Scopes =
            {
                SessionsReadScope,
                SessionsCreateScope,
                SessionsOperateScope,
            },
        },
    ];

    public static IReadOnlyCollection<Client> CreateDevelopmentClients(string clientSecret) =>
    [
        new()
        {
            ClientId = DevelopmentClientId,
            ClientName = "Akos Fabric development operator",
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            ClientSecrets = { new Secret(clientSecret.Sha256()) },
            AllowedScopes =
            {
                SessionsReadScope,
                SessionsCreateScope,
                SessionsOperateScope,
            },
            AccessTokenType = AccessTokenType.Jwt,
            AccessTokenLifetime = 15 * 60,
            IncludeJwtId = true,
            ClientClaimsPrefix = string.Empty,
            Claims =
            {
                new ClientClaim(JwtClaimTypes.Subject, DevelopmentSubject),
            },
        },
    ];
}
