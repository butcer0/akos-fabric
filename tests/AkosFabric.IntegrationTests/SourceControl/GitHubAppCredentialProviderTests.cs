using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.SourceControl.GitHub;

namespace AkosFabric.IntegrationTests.SourceControl;

public sealed class GitHubAppCredentialProviderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExchangesValidSignedAppJwtForNormalizedCredential()
    {
        using RSA signingKey = RSA.Create(2048);
        string keyPath = WritePrivateKey(signingKey);
        CapturedRequest? captured = null;
        const string installationToken = "ghs_installation_secret";

        try
        {
            using var handler = new RecordingHandler(async request =>
            {
                captured = await CapturedRequest.CreateAsync(request);
                return JsonResponse(
                    $$"""
                    {
                      "token": "{{installationToken}}",
                      "expires_at": "2026-07-23T17:00:00Z"
                    }
                    """);
            });
            using var httpClient = new HttpClient(handler);
            var provider = new GitHubAppCredentialProvider(
                httpClient,
                ValidOptions(keyPath),
                new FixedTimeProvider(Now));

            SourceControlCredential credential =
                await provider.GetCredentialAsync(
                    Repository(),
                    new SourceControlPermissionSet(true, true, true),
                    CancellationToken.None);

            Assert.Equal("x-access-token", credential.Username);
            Assert.Equal(installationToken, credential.Secret);
            Assert.Equal(
                new DateTimeOffset(
                    2026,
                    7,
                    23,
                    17,
                    0,
                    0,
                    TimeSpan.Zero),
                credential.ExpiresAt);
            Assert.DoesNotContain(
                installationToken,
                credential.ToString(),
                StringComparison.Ordinal);

            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.Equal(
                "https://api.github.test/app/installations/456/access_tokens",
                captured.Uri.AbsoluteUri);
            Assert.Equal("Bearer", captured.AuthorizationScheme);
            Assert.DoesNotContain(
                installationToken,
                captured.Uri.AbsoluteUri,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                installationToken,
                captured.Body,
                StringComparison.Ordinal);

            using JsonDocument requestBody =
                JsonDocument.Parse(captured.Body);
            JsonElement permissions =
                requestBody.RootElement.GetProperty("permissions");
            Assert.Equal(
                "write",
                permissions.GetProperty("contents").GetString());
            Assert.Equal(
                "write",
                permissions.GetProperty("pull_requests").GetString());

            AssertValidAppJwt(
                captured.AuthorizationParameter,
                signingKey);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void StartupValidationRejectsInvalidConfigurationWithoutKeyMaterial()
    {
        const string privateMaterial = "not-a-private-key-secret";
        string keyPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.pem");
        File.WriteAllText(keyPath, privateMaterial);

        try
        {
            var options = new GitHubOptions
            {
                ApiBaseUrl = new Uri("http://api.github.test/"),
                AppId = "123",
                InstallationId = 456,
                PrivateKeyPath = keyPath,
            };

            GitHubOptionsException exception =
                Assert.Throws<GitHubOptionsException>(options.Validate);

            Assert.Contains(
                nameof(GitHubOptions.ApiBaseUrl),
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateMaterial,
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task FailedExchangeDoesNotIncludeJwtOrResponseBodyInException()
    {
        using RSA signingKey = RSA.Create(2048);
        string keyPath = WritePrivateKey(signingKey);
        string? jwt = null;
        const string responseSecret = "server-returned-secret";

        try
        {
            using var handler = new RecordingHandler(request =>
            {
                jwt = request.Headers.Authorization?.Parameter;
                return Task.FromResult(
                    JsonResponse(
                        $$"""{"message":"{{responseSecret}}"}""",
                        HttpStatusCode.Unauthorized));
            });
            using var httpClient = new HttpClient(handler);
            var provider = new GitHubAppCredentialProvider(
                httpClient,
                ValidOptions(keyPath),
                new FixedTimeProvider(Now));

            GitHubRequestException exception =
                await Assert.ThrowsAsync<GitHubRequestException>(
                    () => provider.GetCredentialAsync(
                        Repository(),
                        new SourceControlPermissionSet(true, false, false),
                        CancellationToken.None));

            Assert.NotNull(jwt);
            Assert.DoesNotContain(
                jwt,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                responseSecret,
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    private static void AssertValidAppJwt(string? jwt, RSA publicKey)
    {
        Assert.False(string.IsNullOrWhiteSpace(jwt));
        string[] segments = jwt.Split('.');
        Assert.Equal(3, segments.Length);

        using JsonDocument header =
            JsonDocument.Parse(Base64UrlDecode(segments[0]));
        Assert.Equal(
            "RS256",
            header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(
            "JWT",
            header.RootElement.GetProperty("typ").GetString());

        using JsonDocument payload =
            JsonDocument.Parse(Base64UrlDecode(segments[1]));
        Assert.Equal(
            "123",
            payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(
            Now.AddSeconds(-60).ToUnixTimeSeconds(),
            payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(
            Now.AddMinutes(9).ToUnixTimeSeconds(),
            payload.RootElement.GetProperty("exp").GetInt64());

        byte[] signedData =
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
        Assert.True(
            publicKey.VerifyData(
                signedData,
                Base64UrlDecode(segments[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string WritePrivateKey(RSA rsa)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        return path;
    }

    private static GitHubOptions ValidOptions(string keyPath) =>
        new()
        {
            ApiBaseUrl = new Uri("https://api.github.test/"),
            AppId = "123",
            InstallationId = 456,
            PrivateKeyPath = keyPath,
            UserAgent = "akos-fabric-tests/1.0",
        };

    private static SourceRepositoryReference Repository() =>
        new(
            "github",
            "butcer0/akos-fabric",
            new Uri("https://github.test/butcer0/akos-fabric.git"));

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.Created) =>
        new(statusCode)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request) =>
            new(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync());
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request);
    }
}
