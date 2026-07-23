using System.Net;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.SourceControl.GitHub;

namespace AkosFabric.IntegrationTests.SourceControl;

public sealed class GitHubSourceControlProviderTests
{
    private const string Secret = "installation-access-secret";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task GetsRemoteBranchHeadUsingEncodedBranchAndReadPermission()
    {
        CapturedRequest? captured = null;
        using var handler = new SequenceHandler(async request =>
        {
            captured = await CapturedRequest.CreateAsync(request);
            return JsonResponse(
                $"{{\"object\":{{\"sha\":\"{Sha}\"}}}}");
        });
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);

        string actual = await provider.GetBranchHeadShaAsync(
            Repository(),
            "agent/KAN-1",
            CancellationToken.None);

        Assert.Equal(Sha, actual);
        Assert.Equal(
            new SourceControlPermissionSet(true, false, false),
            Assert.Single(credentials.RequestedPermissions));
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Contains(
            "/git/ref/heads/agent%2FKAN-1",
            captured.Uri.AbsoluteUri,
            StringComparison.Ordinal);
        AssertCredentialOnlyInAuthorization(captured);
    }

    [Fact]
    public async Task FindsOpenDraftOrRegularRequestByHeadBranch()
    {
        using var handler = new SequenceHandler(
            request => Task.FromResult(
                JsonResponse(
                    $$"""
                    [{
                      "id": 9876,
                      "number": 42,
                      "html_url": "https://github.test/butcer0/akos-fabric/pull/42",
                      "draft": false,
                      "head": {"sha": "{{Sha}}"}
                    }]
                    """)));
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);

        ChangeRequestReference? result =
            await provider.FindOpenChangeRequestAsync(
                Repository(),
                "agent/KAN-1",
                CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("github", result.ProviderName);
        Assert.Equal("9876", result.ProviderId);
        Assert.Equal("42", result.Number);
        Assert.Equal(Sha, result.RevisionSha);
        CapturedRequest captured = Assert.Single(handler.Requests);
        Assert.Contains(
            "state=open",
            captured.Uri.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            "head=butcer0%3Aagent%2FKAN-1",
            captured.Uri.AbsoluteUri,
            StringComparison.Ordinal);
        AssertCredentialOnlyInAuthorization(captured);
    }

    [Fact]
    public async Task ExistingOpenRequestMakesDraftCreationIdempotent()
    {
        using var handler = new SequenceHandler(
            request => Task.FromResult(JsonResponse(PullRequestJson())));
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);

        ChangeRequestReference result =
            await provider.CreateChangeRequestAsync(
                CreateRequest(),
                CancellationToken.None);

        Assert.Equal("42", result.Number);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(
            new SourceControlPermissionSet(true, false, true),
            Assert.Single(credentials.RequestedPermissions));
    }

    [Fact]
    public async Task CreatesDraftWithProviderNeutralFieldsAndNoCredentialLeak()
    {
        int call = 0;
        using var handler = new SequenceHandler(request =>
        {
            call++;
            return Task.FromResult(
                call == 1
                    ? JsonResponse("[]")
                    : JsonResponse(PullRequestObjectJson(), HttpStatusCode.Created));
        });
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);

        ChangeRequestReference result =
            await provider.CreateChangeRequestAsync(
                CreateRequest(),
                CancellationToken.None);

        Assert.Equal(Sha, result.RevisionSha);
        Assert.Equal(2, handler.Requests.Count);
        CapturedRequest post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal(
            "https://api.github.test/repos/butcer0/akos-fabric/pulls",
            post.Uri.AbsoluteUri);
        AssertCredentialOnlyInAuthorization(post);

        using JsonDocument payload = JsonDocument.Parse(post.Body);
        Assert.True(payload.RootElement.GetProperty("draft").GetBoolean());
        Assert.Equal(
            "agent/KAN-1",
            payload.RootElement.GetProperty("head").GetString());
        Assert.Equal(
            "main",
            payload.RootElement.GetProperty("base").GetString());
        Assert.Equal(
            "KAN-1: durable fix",
            payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "Neutral acceptance evidence",
            payload.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task CreationRaceReturnsRequestFoundAfterUnprocessableEntity()
    {
        int call = 0;
        using var handler = new SequenceHandler(request =>
        {
            call++;
            return Task.FromResult(call switch
            {
                1 => JsonResponse("[]"),
                2 => JsonResponse(
                    """{"message":"already exists"}""",
                    HttpStatusCode.UnprocessableEntity),
                _ => JsonResponse(PullRequestJson()),
            });
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(
            httpClient,
            new FakeCredentialProvider());

        ChangeRequestReference result =
            await provider.CreateChangeRequestAsync(
                CreateRequest(),
                CancellationToken.None);

        Assert.Equal("42", result.Number);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post, HttpMethod.Get],
            handler.Requests.Select(item => item.Method));
    }

    [Fact]
    public async Task InformationalReviewIsCreatedThenUpdatedAtExactHead()
    {
        int call = 0;
        string? publishedBody = null;
        using var handler = new SequenceHandler(async request =>
        {
            call++;
            switch (call)
            {
                case 1:
                case 4:
                    return JsonResponse(PullRequestObjectJson());
                case 2:
                    return JsonResponse("[]");
                case 3:
                    {
                        using JsonDocument payload = JsonDocument.Parse(
                            await request.Content!.ReadAsStringAsync());
                        publishedBody = payload.RootElement
                            .GetProperty("body")
                            .GetString();
                        return JsonResponse(
                            CommentJson(777, publishedBody!),
                            HttpStatusCode.Created);
                    }
                case 5:
                    return JsonResponse(
                        $"[{CommentJson(777, publishedBody!)}]");
                case 6:
                    {
                        using JsonDocument payload = JsonDocument.Parse(
                            await request.Content!.ReadAsStringAsync());
                        publishedBody = payload.RootElement
                            .GetProperty("body")
                            .GetString();
                        return JsonResponse(CommentJson(777, publishedBody!));
                    }
                default:
                    throw new InvalidOperationException(
                        "Unexpected GitHub request.");
            }
        });
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);

        ChangeRequestReviewResult created =
            await provider.UpsertInformationalReviewAsync(
                Review("First deterministic review"),
                CancellationToken.None);
        ChangeRequestReviewResult updated =
            await provider.UpsertInformationalReviewAsync(
                Review("Updated deterministic review"),
                CancellationToken.None);

        Assert.Equal(ChangeRequestReviewPublication.Created, created.Publication);
        Assert.Equal(ChangeRequestReviewPublication.Updated, updated.Publication);
        Assert.Equal("github", updated.ProviderName);
        Assert.Equal("777", updated.ProviderId);
        Assert.Equal("9876", updated.ChangeRequestId);
        Assert.Equal(Sha, updated.RevisionSha);
        Assert.Equal(
            new Uri(
                "https://github.test/butcer0/akos-fabric/pull/42" +
                "#issuecomment-777"),
            updated.Url);
        Assert.Equal(
            [
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Post,
                HttpMethod.Get,
                HttpMethod.Get,
                HttpMethod.Patch,
            ],
            handler.Requests.Select(item => item.Method));
        Assert.Equal(
            "https://api.github.test/repos/butcer0/akos-fabric/" +
            "issues/comments/777",
            handler.Requests[5].Uri.AbsoluteUri);
        Assert.Contains(
            "<!-- akos-fabric:informational-review:v1 -->",
            publishedBody,
            StringComparison.Ordinal);
        Assert.Contains(Sha, publishedBody, StringComparison.Ordinal);
        Assert.Contains(
            "Updated deterministic review",
            publishedBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "APPROVE",
            publishedBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(
            credentials.RequestedPermissions,
            permissions => Assert.Equal(
                new SourceControlPermissionSet(
                    true,
                    false,
                    false,
                    true),
                permissions));
        Assert.All(handler.Requests, AssertCredentialOnlyInAuthorization);
    }

    [Fact]
    public async Task InformationalReviewRejectsChangedHeadBeforeCommentReadOrWrite()
    {
        string changedSha = new('f', 40);
        using var handler = new SequenceHandler(
            request => Task.FromResult(
                JsonResponse(PullRequestObjectJson(changedSha))));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(
            httpClient,
            new FakeCredentialProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.UpsertInformationalReviewAsync(
                Review("Review for stale revision"),
                CancellationToken.None));

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.github.test/repos/butcer0/akos-fabric/pulls/42",
            request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task InformationalReviewRefusesAmbiguousExistingComments()
    {
        const string marker =
            "<!-- akos-fabric:informational-review:v1 -->";
        using var handler = new SequenceHandler(request =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/pulls/42",
                    StringComparison.Ordinal)
                    ? JsonResponse(PullRequestObjectJson())
                    : JsonResponse(
                        $"[{CommentJson(777, marker)}," +
                        $"{CommentJson(778, marker)}]")));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(
            httpClient,
            new FakeCredentialProvider());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.UpsertInformationalReviewAsync(
                Review("Must not add a third comment"),
                CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task ErrorAndPayloadGuardsDoNotExposeCredential()
    {
        int call = 0;
        using var handler = new SequenceHandler(request =>
        {
            call++;
            return Task.FromResult(
                call == 1
                    ? JsonResponse("[]")
                    : JsonResponse(
                        $$"""{"message":"{{Secret}}"}""",
                        HttpStatusCode.InternalServerError));
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(
            httpClient,
            new FakeCredentialProvider());

        GitHubRequestException exception =
            await Assert.ThrowsAsync<GitHubRequestException>(
                () => provider.CreateChangeRequestAsync(
                    CreateRequest(),
                    CancellationToken.None));
        Assert.DoesNotContain(
            Secret,
            exception.ToString(),
            StringComparison.Ordinal);

        using var guardHandler = new SequenceHandler(
            request => Task.FromResult(JsonResponse("[]")));
        using var guardClient = new HttpClient(guardHandler);
        var guardedProvider = CreateProvider(
            guardClient,
            new FakeCredentialProvider());
        CreateChangeRequest leakingRequest =
            CreateRequest() with { Body = $"Never print {Secret}" };

        InvalidOperationException guardException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => guardedProvider.CreateChangeRequestAsync(
                    leakingRequest,
                    CancellationToken.None));
        Assert.DoesNotContain(
            Secret,
            guardException.ToString(),
            StringComparison.Ordinal);
        Assert.Single(guardHandler.Requests);
    }

    [Fact]
    public async Task EmbeddedCloneCredentialIsRejectedBeforeTokenAcquisition()
    {
        using var handler = new SequenceHandler(
            request => Task.FromResult(JsonResponse("{}")));
        var credentials = new FakeCredentialProvider();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, credentials);
        var unsafeRepository = new SourceRepositoryReference(
            "github",
            "butcer0/akos-fabric",
            new Uri(
                "https://embedded-secret@github.test/butcer0/akos-fabric.git"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetBranchHeadShaAsync(
                unsafeRepository,
                "main",
                CancellationToken.None));

        Assert.Empty(credentials.RequestedPermissions);
        Assert.Empty(handler.Requests);
    }

    private static GitHubSourceControlProvider CreateProvider(
        HttpClient httpClient,
        ISourceControlCredentialProvider credentials) =>
        new(
            httpClient,
            credentials,
            new GitHubOptions
            {
                ApiBaseUrl = new Uri("https://api.github.test/"),
                AppId = "123",
                InstallationId = 456,
                UserAgent = "akos-fabric-tests/1.0",
            });

    private static SourceRepositoryReference Repository() =>
        new(
            "github",
            "butcer0/akos-fabric",
            new Uri("https://github.test/butcer0/akos-fabric.git"));

    private static CreateChangeRequest CreateRequest() =>
        new(
            Repository(),
            "agent/KAN-1",
            "main",
            "KAN-1: durable fix",
            "Neutral acceptance evidence",
            true);

    private static string PullRequestJson() =>
        $"[{PullRequestObjectJson()}]";

    private static string PullRequestObjectJson(string sha = Sha) =>
        $$"""
        {
          "id": 9876,
          "number": 42,
          "html_url": "https://github.test/butcer0/akos-fabric/pull/42",
          "draft": true,
          "head": {"sha": "{{sha}}"}
        }
        """;

    private static ChangeRequestReview Review(string markdown) =>
        new(
            Repository(),
            new ChangeRequestReference(
                "github",
                "9876",
                "42",
                new Uri(
                    "https://github.test/butcer0/akos-fabric/pull/42"),
                Sha),
            markdown);

    private static string CommentJson(long id, string body) =>
        JsonSerializer.Serialize(
            new
            {
                id,
                body,
                html_url =
                    "https://github.test/butcer0/akos-fabric/pull/42" +
                    $"#issuecomment-{id}",
            });

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"),
        };

    private static void AssertCredentialOnlyInAuthorization(
        CapturedRequest request)
    {
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Secret, request.AuthorizationParameter);
        Assert.DoesNotContain(
            Secret,
            request.Uri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Secret,
            request.Body,
            StringComparison.Ordinal);
    }

    private sealed class FakeCredentialProvider
        : ISourceControlCredentialProvider
    {
        public string ProviderName => "github";

        public List<SourceControlPermissionSet> RequestedPermissions { get; } =
            [];

        public Task<SourceControlCredential> GetCredentialAsync(
            SourceRepositoryReference repository,
            SourceControlPermissionSet permissions,
            CancellationToken cancellationToken)
        {
            RequestedPermissions.Add(permissions);
            return Task.FromResult(
                new SourceControlCredential(
                    "x-access-token",
                    Secret,
                    DateTimeOffset.UtcNow.AddMinutes(30)));
        }
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

    private sealed class SequenceHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(await CapturedRequest.CreateAsync(request));
            return await callback(request);
        }
    }
}
