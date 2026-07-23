using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AkosFabric.Api.Security;
using AkosFabric.Identity;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AkosFabric.IntegrationTests.Security;

public sealed class IdentityProtocolTests : IClassFixture<DevelopmentIdentityFixture>
{
    private readonly DevelopmentIdentityFixture fixture;

    public IdentityProtocolTests(DevelopmentIdentityFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task RealClientCredentialsTokenIsAcceptedThroughDiscoveryAndJwks()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var identityClient =
            DevelopmentIdentityFixture.CreateIdentityClient(identityServer);

        using var discoveryResponse = await identityClient.GetAsync(
            "/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, discoveryResponse.StatusCode);

        using var discovery = JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStreamAsync());
        var discoveryRoot = discovery.RootElement;
        Assert.Equal(
            DevelopmentIdentityFixture.Authority,
            discoveryRoot.GetProperty("issuer").GetString());
        Assert.Equal(
            $"{DevelopmentIdentityFixture.Authority}/connect/token",
            discoveryRoot.GetProperty("token_endpoint").GetString());
        Assert.Equal(
            $"{DevelopmentIdentityFixture.Authority}/.well-known/openid-configuration/jwks",
            discoveryRoot.GetProperty("jwks_uri").GetString());

        var tokenResponse = await identityClient.RequestClientCredentialsTokenAsync(
            new ClientCredentialsTokenRequest
            {
                Address = discoveryRoot.GetProperty("token_endpoint").GetString(),
                ClientId = IdentityConfiguration.DevelopmentClientId,
                ClientSecret = DevelopmentIdentityFixture.ClientSecret,
                Scope = string.Join(
                    ' ',
                    IdentityConfiguration.SessionsReadScope,
                    IdentityConfiguration.SessionsCreateScope,
                    IdentityConfiguration.SessionsOperateScope),
            });

        Assert.False(tokenResponse.IsError, tokenResponse.Error);
        Assert.False(string.IsNullOrWhiteSpace(tokenResponse.AccessToken));
        Assert.Equal(15 * 60, tokenResponse.ExpiresIn);

        var token = new JsonWebTokenHandler().ReadJsonWebToken(tokenResponse.AccessToken);
        Assert.Equal("at+jwt", token.Typ);
        Assert.Equal(DevelopmentIdentityFixture.Authority, token.Issuer);
        Assert.Contains(IdentityConfiguration.ApiAudience, token.Audiences);
        Assert.Equal(
            IdentityConfiguration.DevelopmentSubject,
            token.GetClaim("sub").Value);
        Assert.Equal(
            IdentityConfiguration.DevelopmentClientId,
            token.GetClaim("client_id").Value);
        Assert.False(string.IsNullOrWhiteSpace(token.GetClaim("iat").Value));
        Assert.False(string.IsNullOrWhiteSpace(token.GetClaim("jti").Value));
        Assert.Contains(
            IdentityConfiguration.SessionsReadScope,
            token.GetClaim("scope").Value.Split(' '));

        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        using var apiClient = api.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenResponse.AccessToken);

        using var readyResponse = await apiClient.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
    }

    [Fact]
    public async Task DevelopmentClientCannotRequestAnUnconfiguredScope()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var identityClient =
            DevelopmentIdentityFixture.CreateIdentityClient(identityServer);

        var response = await identityClient.RequestClientCredentialsTokenAsync(
            new ClientCredentialsTokenRequest
            {
                Address = $"{DevelopmentIdentityFixture.Authority}/connect/token",
                ClientId = IdentityConfiguration.DevelopmentClientId,
                ClientSecret = DevelopmentIdentityFixture.ClientSecret,
                Scope = "agent.sessions.read unregistered.scope",
            });

        Assert.True(response.IsError);
        Assert.Equal("invalid_scope", response.Error);
        Assert.Null(response.AccessToken);
    }

    [Fact]
    public async Task CryptographicallyInvalidOrSemanticallyInvalidTokensReturnUnauthorized()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var identityClient =
            DevelopmentIdentityFixture.CreateIdentityClient(identityServer);
        using var discoveryResponse = await identityClient.GetAsync(
            "/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, discoveryResponse.StatusCode);

        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        using var apiClient = api.CreateClient();
        var signingKey = DevelopmentSigningKeyStore.LoadOrCreate(
            fixture.SigningKeyPath);
        var wrongSigningKey = new RsaSecurityKey(
            System.Security.Cryptography.RSA.Create(3072))
        {
            KeyId = Guid.NewGuid().ToString("N"),
        };
        var now = DateTime.UtcNow;

        var invalidTokens = new Dictionary<string, string>
        {
            ["invalid signature"] = CreateAccessToken(
                wrongSigningKey,
                now: now),
            ["wrong issuer"] = CreateAccessToken(
                signingKey,
                issuer: "https://wrong-issuer.test",
                now: now),
            ["wrong audience"] = CreateAccessToken(
                signingKey,
                audience: "wrong-audience",
                now: now),
            ["expired"] = CreateAccessToken(
                signingKey,
                notBefore: now.AddMinutes(-10),
                expires: now.AddMinutes(-2),
                now: now),
            ["future nbf"] = CreateAccessToken(
                signingKey,
                notBefore: now.AddMinutes(5),
                expires: now.AddMinutes(20),
                now: now),
            ["wrong typ"] = CreateAccessToken(
                signingKey,
                tokenType: "JWT",
                now: now),
            ["missing sub"] = CreateAccessToken(
                signingKey,
                omittedClaims: ["sub"],
                now: now),
            ["missing iat"] = CreateAccessToken(
                signingKey,
                omittedClaims: ["iat"],
                now: now),
            ["missing jti"] = CreateAccessToken(
                signingKey,
                omittedClaims: ["jti"],
                now: now),
            ["missing client identity"] = CreateAccessToken(
                signingKey,
                omittedClaims: ["client_id"],
                now: now),
            ["missing scope"] = CreateAccessToken(
                signingKey,
                omittedClaims: ["scope"],
                now: now),
        };

        foreach (var (caseName, token) in invalidTokens)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/health/ready");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            using var response = await apiClient.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected 401 for {caseName}, received {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task PersistedSigningAndDataProtectionKeysSurviveIdentityServerRestart()
    {
        string accessToken;
        string firstKeyId;
        Dictionary<string, byte[]> firstDataProtectionKeys;

        using (var firstIdentityServer = fixture.CreateIdentityServer())
        using (var firstClient =
               DevelopmentIdentityFixture.CreateIdentityClient(firstIdentityServer))
        {
            var tokenResponse = await firstClient.RequestClientCredentialsTokenAsync(
                DevelopmentTokenRequest());
            Assert.False(tokenResponse.IsError, tokenResponse.Error);
            accessToken = tokenResponse.AccessToken!;
            firstKeyId = new JsonWebTokenHandler()
                .ReadJsonWebToken(accessToken)
                .Kid;
            firstDataProtectionKeys = ReadDataProtectionKeys(
                fixture.DataProtectionKeyPath);
            Assert.NotEmpty(firstDataProtectionKeys);
        }

        using var restartedIdentityServer = fixture.CreateIdentityServer();
        using var restartedClient =
            DevelopmentIdentityFixture.CreateIdentityClient(restartedIdentityServer);
        var restartedTokenResponse =
            await restartedClient.RequestClientCredentialsTokenAsync(
                DevelopmentTokenRequest());
        Assert.False(restartedTokenResponse.IsError, restartedTokenResponse.Error);
        Assert.Equal(
            firstKeyId,
            new JsonWebTokenHandler()
                .ReadJsonWebToken(restartedTokenResponse.AccessToken)
                .Kid);
        var restartedDataProtectionKeys = ReadDataProtectionKeys(
            fixture.DataProtectionKeyPath);
        Assert.Equal(
            firstDataProtectionKeys.Keys.Order(),
            restartedDataProtectionKeys.Keys.Order());
        foreach (var (fileName, contents) in firstDataProtectionKeys)
        {
            Assert.Equal(contents, restartedDataProtectionKeys[fileName]);
        }

        using var api =
            DevelopmentIdentityFixture.CreateApi(restartedIdentityServer);
        using var apiClient = api.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await apiClient.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LivenessIsAnonymousAndReadinessRequiresAuthentication()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        using var client = api.CreateClient();

        using var livenessResponse = await client.GetAsync("/health/live");
        using var readinessResponse = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, livenessResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, readinessResponse.StatusCode);
    }

    [Fact]
    public void JwtBearerConfigurationRequiresAtJwtAndStrictValidation()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);

        var options = api.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var validation = options.TokenValidationParameters;

        Assert.False(options.MapInboundClaims);
        Assert.False(options.IncludeErrorDetails);
        Assert.True(validation.ValidateIssuer);
        Assert.True(validation.ValidateAudience);
        Assert.True(validation.ValidateIssuerSigningKey);
        Assert.True(validation.ValidateLifetime);
        Assert.True(validation.RequireSignedTokens);
        Assert.True(validation.RequireExpirationTime);
        Assert.Equal(["at+jwt"], validation.ValidTypes);
        Assert.Equal(TimeSpan.FromMinutes(1), validation.ClockSkew);
    }

    [Fact]
    public async Task ScopePoliciesAcceptScopeAndScpButRejectMissingScope()
    {
        using var identityServer = fixture.CreateIdentityServer();
        using var api = DevelopmentIdentityFixture.CreateApi(identityServer);
        var authorization = api.Services.GetRequiredService<IAuthorizationService>();

        var scopePrincipal = PrincipalWithScope(
            "scope",
            "agent.sessions.read agent.sessions.create");
        var scpPrincipal = PrincipalWithScope(
            "scp",
            "agent.sessions.operate");
        var wrongScopePrincipal = PrincipalWithScope(
            "scope",
            "agent.sessions.read");

        Assert.True((await authorization.AuthorizeAsync(
            scopePrincipal,
            resource: null,
            AgentControlPolicies.SessionsCreate)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            scpPrincipal,
            resource: null,
            AgentControlPolicies.SessionsOperate)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(
            wrongScopePrincipal,
            resource: null,
            AgentControlPolicies.SessionsCreate)).Succeeded);
    }

    private static System.Security.Claims.ClaimsPrincipal PrincipalWithScope(
        string claimType,
        string value) =>
        new(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(claimType, value)],
                JwtBearerDefaults.AuthenticationScheme));

    private static ClientCredentialsTokenRequest DevelopmentTokenRequest() =>
        new()
        {
            Address = $"{DevelopmentIdentityFixture.Authority}/connect/token",
            ClientId = IdentityConfiguration.DevelopmentClientId,
            ClientSecret = DevelopmentIdentityFixture.ClientSecret,
            Scope = IdentityConfiguration.SessionsReadScope,
        };

    private static string CreateAccessToken(
        RsaSecurityKey signingKey,
        string issuer = DevelopmentIdentityFixture.Authority,
        string audience = IdentityConfiguration.ApiAudience,
        string tokenType = "at+jwt",
        DateTime? notBefore = null,
        DateTime? expires = null,
        IReadOnlyCollection<string>? omittedClaims = null,
        DateTime? now = null)
    {
        var effectiveNow = now ?? DateTime.UtcNow;
        var omitted = omittedClaims ?? [];
        var claims = new Dictionary<string, object>
        {
            ["sub"] = IdentityConfiguration.DevelopmentSubject,
            ["iat"] = EpochTime.GetIntDate(effectiveNow),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["client_id"] = IdentityConfiguration.DevelopmentClientId,
            ["scope"] = IdentityConfiguration.SessionsReadScope,
        };

        foreach (var omittedClaim in omitted)
        {
            claims.Remove(omittedClaim);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = notBefore ?? effectiveNow.AddMinutes(-1),
            Expires = expires ?? effectiveNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                signingKey,
                SecurityAlgorithms.RsaSha256),
            TokenType = tokenType,
        };

        return new JsonWebTokenHandler
        {
            SetDefaultTimesOnTokenCreation = false,
        }.CreateToken(descriptor);
    }

    private static Dictionary<string, byte[]> ReadDataProtectionKeys(string path) =>
        Directory
            .EnumerateFiles(path)
            .ToDictionary(
                file => Path.GetFileName(file)
                    ?? throw new InvalidOperationException(
                        "A Data Protection key path had no file name."),
                File.ReadAllBytes,
                StringComparer.Ordinal);
}
