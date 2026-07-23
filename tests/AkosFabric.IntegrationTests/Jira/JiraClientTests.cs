using System.Net;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Infrastructure.Jira;

namespace AkosFabric.IntegrationTests.Jira;

public sealed class JiraClientTests
{
    [Fact]
    public async Task SearchIssuesPaginatesInServerOrderAndPreservesSnapshots()
    {
        const string firstIssue =
            """{ "id" : "10001", "key" : "FAB-2", "fields" : { "custom_summary" : "Second priority", "custom_description" : { "version": 1, "type": "doc", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "First line" } ] }, { "type": "paragraph", "content": [ { "type": "text", "text": "Second line" } ] } ] }, "custom_type" : { "name": "Bug" }, "custom_status" : { "name": "Ready" }, "custom_priority" : { "name": "Highest" }, "custom_labels" : [ "agent", "backend" ], "custom_updated" : "2026-07-23T13:14:15.123+00:00" } }""";
        const string secondIssue =
            """{"id":"10002","key":"FAB-1","fields":{"custom_summary":"Older","custom_description":"Plain requirements","custom_type":{"name":"Story"},"custom_status":{"name":"Ready"},"custom_priority":null,"custom_labels":[],"custom_updated":"2026-07-22T09:10:11Z"}}""";
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                """{"issues":[""" + firstIssue
                + """],"isLast":false,"nextPageToken":"page-2"}"""),
            JsonResponse(
                """{"issues":[""" + secondIssue
                + """],"isLast":true}"""),
        ]);
        var requests = new List<CapturedRequest>();
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            requests.Add(await CaptureAsync(request));
            return responses.Dequeue();
        }));
        JiraClient client = CreateClient(httpClient);

        IReadOnlyList<JiraIssueSnapshot> result = await client.SearchIssuesAsync(
            CreateProfile(),
            CancellationToken.None);

        Assert.Equal(["FAB-2", "FAB-1"], result.Select(issue => issue.Key));
        Assert.Equal("First line\nSecond line", result[0].Description);
        Assert.Equal(["agent", "backend"], result[0].Labels);
        Assert.Equal("Highest", result[0].Priority);
        Assert.Null(result[1].Priority);
        Assert.Equal(firstIssue, result[0].SnapshotJson);
        Assert.Equal(secondIssue, result[1].SnapshotJson);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 23, 13, 14, 15, 123, TimeSpan.Zero),
            result[0].UpdatedAt);

        Assert.Equal(2, requests.Count);
        Assert.All(
            requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://jira.example.test/base/rest/api/3/search/jql",
                    request.Uri.AbsoluteUri);
                Assert.Equal("Bearer", request.AuthorizationScheme);
                Assert.Equal("jira-token", request.AuthorizationParameter);
            });

        using JsonDocument firstRequest = JsonDocument.Parse(requests[0].Body!);
        JsonElement firstRoot = firstRequest.RootElement;
        Assert.Equal("project = FAB ORDER BY priority DESC, created ASC",
            firstRoot.GetProperty("jql").GetString());
        Assert.Equal(100, firstRoot.GetProperty("maxResults").GetInt32());
        Assert.False(firstRoot.TryGetProperty("nextPageToken", out _));
        Assert.Equal(
            [
                "id",
                "key",
                "custom_summary",
                "custom_description",
                "custom_type",
                "custom_status",
                "custom_priority",
                "custom_labels",
                "custom_updated",
            ],
            firstRoot.GetProperty("fields")
                .EnumerateArray()
                .Select(field => field.GetString()));

        using JsonDocument secondRequest = JsonDocument.Parse(requests[1].Body!);
        Assert.Equal(
            "page-2",
            secondRequest.RootElement.GetProperty("nextPageToken").GetString());
    }

    [Fact]
    public async Task FindIssueUsesMappedFieldsAndReturnsNullForNotFound()
    {
        CapturedRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            captured = await CaptureAsync(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        JiraClient client = CreateClient(httpClient);

        JiraIssueSnapshot? result = await client.FindIssueAsync(
            CreateProfile(),
            "FAB-19",
            CancellationToken.None);

        Assert.Null(result);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.StartsWith(
            "https://jira.example.test/base/rest/api/3/issue/FAB-19?fields=",
            captured.Uri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Contains("custom_description", captured.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransitionIssueResolvesConfiguredStatusNameAndPostsItsId()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                """
                {
                  "transitions": [
                    {"id":"31","name":"Start progress","to":{"name":"In Progress"}},
                    {"id":"72","name":"Review","to":{"name":"Peer Review"}}
                  ]
                }
                """),
            new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        var requests = new List<CapturedRequest>();
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            requests.Add(await CaptureAsync(request));
            return responses.Dequeue();
        }));
        JiraClient client = CreateClient(httpClient);
        JiraRepositoryProfile profile = CreateProfile() with
        {
            Workflow = CreateProfile().Workflow with
            {
                ReviewStatus = "Peer Review",
            },
        };

        JiraTransitionResult result = await client.TransitionIssueAsync(
            profile,
            "FAB-19",
            JiraWorkflowTarget.Review,
            CancellationToken.None);

        Assert.Equal(JiraTransitionOutcome.Applied, result.Outcome);
        Assert.Equal("72", result.TransitionId);
        Assert.Collection(
            requests,
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using JsonDocument body = JsonDocument.Parse(request.Body!);
                Assert.Equal(
                    "72",
                    body.RootElement
                        .GetProperty("transition")
                        .GetProperty("id")
                        .GetString());
            });
    }

    [Fact]
    public async Task TransitionIssueReturnsUnavailableWithoutPosting()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            requestCount++;
            return Task.FromResult(JsonResponse(
                """{"transitions":[{"id":"31","to":{"name":"In Progress"}}]}"""));
        }));
        JiraClient client = CreateClient(httpClient);

        JiraTransitionResult result = await client.TransitionIssueAsync(
            CreateProfile(),
            "FAB-19",
            JiraWorkflowTarget.Review,
            CancellationToken.None);

        Assert.Equal(JiraTransitionOutcome.Unavailable, result.Outcome);
        Assert.Null(result.TransitionId);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task AddCommentSendsAtlassianDocumentFormat()
    {
        CapturedRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            captured = await CaptureAsync(request);
            return JsonResponse("""{"id":"1234"}""", HttpStatusCode.Created);
        }));
        JiraClient client = CreateClient(httpClient);

        await client.AddCommentAsync(
            CreateProfile(),
            "FAB-19",
            "Draft change request: https://git.example.test/changes/8",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(
            "https://jira.example.test/base/rest/api/3/issue/FAB-19/comment",
            captured.Uri.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(captured.Body!);
        JsonElement document = body.RootElement.GetProperty("body");
        Assert.Equal(1, document.GetProperty("version").GetInt32());
        Assert.Equal("doc", document.GetProperty("type").GetString());
        Assert.Equal(
            "Draft change request: https://git.example.test/changes/8",
            document.GetProperty("content")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, JiraFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, JiraFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, JiraFailureKind.Terminal)]
    [InlineData(HttpStatusCode.Unauthorized, JiraFailureKind.Terminal)]
    public async Task SearchIssuesClassifiesHttpFailures(
        HttpStatusCode statusCode,
        JiraFailureKind expectedKind)
    {
        using var httpClient = new HttpClient(new DelegateHandler(
            _ => Task.FromResult(new HttpResponseMessage(statusCode))));
        JiraClient client = CreateClient(httpClient);

        JiraClientException exception = await Assert.ThrowsAsync<JiraClientException>(
            () => client.SearchIssuesAsync(CreateProfile(), CancellationToken.None));

        Assert.Equal(expectedKind, exception.FailureKind);
        Assert.Equal(statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task SearchIssuesClassifiesTransportFailureAsTransient()
    {
        using var httpClient = new HttpClient(new DelegateHandler(
            _ => throw new HttpRequestException("synthetic connection failure")));
        JiraClient client = CreateClient(httpClient);

        JiraClientException exception = await Assert.ThrowsAsync<JiraClientException>(
            () => client.SearchIssuesAsync(CreateProfile(), CancellationToken.None));

        Assert.Equal(JiraFailureKind.Transient, exception.FailureKind);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task SearchIssuesRejectsRepeatedPaginationToken()
    {
        using var httpClient = new HttpClient(new DelegateHandler(
            _ => Task.FromResult(JsonResponse(
                """{"issues":[],"isLast":false,"nextPageToken":"same"}"""))));
        JiraClient client = CreateClient(httpClient);

        JiraClientException exception = await Assert.ThrowsAsync<JiraClientException>(
            () => client.SearchIssuesAsync(CreateProfile(), CancellationToken.None));

        Assert.Equal(JiraFailureKind.Terminal, exception.FailureKind);
        Assert.Contains("repeated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindIssueRejectsIssueOutsideConfiguredProjectWithoutCallingJira()
    {
        var called = false;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            called = true;
            return Task.FromResult(JsonResponse("{}"));
        }));
        JiraClient client = CreateClient(httpClient);

        JiraClientException exception = await Assert.ThrowsAsync<JiraClientException>(
            () => client.FindIssueAsync(
                CreateProfile(),
                "OTHER-1",
                CancellationToken.None));

        Assert.Equal(JiraFailureKind.Terminal, exception.FailureKind);
        Assert.False(called);
    }

    [Fact]
    public async Task SearchIssuesPropagatesCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        using var httpClient = new HttpClient(new DelegateHandler(
            (_, cancellationToken) =>
                Task.FromCanceled<HttpResponseMessage>(cancellationToken)));
        JiraClient client = CreateClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SearchIssuesAsync(
                CreateProfile(),
                cancellationSource.Token));
    }

    private static JiraClient CreateClient(HttpClient httpClient)
    {
        var sites = new Dictionary<string, JiraSiteOptions>(StringComparer.Ordinal)
        {
            ["tenant-one"] = new(
                new Uri("https://jira.example.test/base"),
                "jira-service-account"),
        };
        return new JiraClient(
            httpClient,
            new StubAccessTokenProvider(),
            new JiraOptions(sites));
    }

    private static JiraRepositoryProfile CreateProfile() =>
        new(
            "tenant-one",
            "FAB",
            ["Story", "Bug"],
            "project = FAB ORDER BY priority DESC, created ASC",
            new JiraFieldProfile(
                "id",
                "key",
                "custom_summary",
                "custom_description",
                "custom_type",
                "custom_status",
                "custom_priority",
                "custom_labels",
                "custom_updated"),
            new JiraWorkflowProfile(
                "default-zero-configuration",
                "Ready",
                "Doing",
                "Review",
                "Complete",
                "Backlog",
                true));

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static async Task<CapturedRequest> CaptureAsync(
        HttpRequestMessage request) =>
        new(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync());

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? Body);

    private sealed class StubAccessTokenProvider : IJiraAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(
            string authenticationProfile,
            CancellationToken cancellationToken)
        {
            Assert.Equal("jira-service-account", authenticationProfile);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("jira-token");
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
