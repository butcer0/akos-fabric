using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.Infrastructure.SourceControl.GitHub;

public sealed class GitHubAppCredentialProvider
    : ISourceControlCredentialProvider
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly GitHubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _privateKey;

    public GitHubAppCredentialProvider(
        HttpClient httpClient,
        GitHubOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _privateKey = options.ValidateAndReadPrivateKey();
    }

    public string ProviderName => "github";

    public async Task<SourceControlCredential> GetCredentialAsync(
        SourceRepositoryReference repository,
        SourceControlPermissionSet permissions,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.SourceControlCredentialAcquire,
            ActivityKind.Client);
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.SourceControlProvider,
            ProviderName);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(permissions);
        EnsureGitHubRepository(repository);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string jwt = CreateAppJwt(now);
        Uri endpoint = GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"app/installations/{_options.InstallationId.ToString(CultureInfo.InvariantCulture)}/access_tokens");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            jwt);
        GitHubHttp.AddStandardHeaders(request, _options.UserAgent);
        request.Content = JsonContent.Create(
            CreatePermissionsRequest(permissions),
            options: SerializerOptions);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubRequestException(
                "create installation access token",
                response.StatusCode);
        }

        await using Stream responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubInstallationTokenResponse? tokenResponse =
            await JsonSerializer.DeserializeAsync<GitHubInstallationTokenResponse>(
                responseStream,
                SerializerOptions,
                cancellationToken);

        if (tokenResponse is null ||
            string.IsNullOrWhiteSpace(tokenResponse.Token) ||
            tokenResponse.ExpiresAt <= now)
        {
            throw new InvalidDataException(
                "GitHub returned an invalid installation access token response.");
        }

        return new SourceControlCredential(
            "x-access-token",
            tokenResponse.Token,
            tokenResponse.ExpiresAt);
    }

    private string CreateAppJwt(DateTimeOffset now)
    {
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" },
            SerializerOptions);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
                exp = now.AddMinutes(9).ToUnixTimeSeconds(),
                iss = _options.AppId,
            },
            SerializerOptions);

        string unsignedToken =
            $"{Base64UrlEncode(header)}.{Base64UrlEncode(payload)}";
        byte[] unsignedBytes = Encoding.ASCII.GetBytes(unsignedToken);

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(_privateKey);
        byte[] signature = rsa.SignData(
            unsignedBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static object CreatePermissionsRequest(
        SourceControlPermissionSet permissions)
    {
        var requestedPermissions = new Dictionary<string, string>(
            StringComparer.Ordinal);

        if (permissions.CanReadRepository || permissions.CanPushBranch)
        {
            requestedPermissions["contents"] =
                permissions.CanPushBranch ? "write" : "read";
        }

        if (permissions.CanCreateChangeRequest
            || permissions.CanPublishChangeRequestReview)
        {
            requestedPermissions["pull_requests"] = "write";
        }

        return new { permissions = requestedPermissions };
    }

    private static void EnsureGitHubRepository(
        SourceRepositoryReference repository) =>
        GitHubHttp.ValidateRepositoryReference(repository);

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record GitHubInstallationTokenResponse(
        string Token,
        [property: JsonPropertyName("expires_at")]
        DateTimeOffset ExpiresAt);
}
