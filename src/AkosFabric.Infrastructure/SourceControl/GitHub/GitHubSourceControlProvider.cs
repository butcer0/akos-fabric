using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.Infrastructure.SourceControl.GitHub;

public sealed class GitHubSourceControlProvider : ISourceControlProvider
{
    private const string InformationalReviewMarker =
        "<!-- akos-fabric:informational-review:v1 -->";
    private static readonly SourceControlPermissionSet ReadPermissions =
        new(true, false, false);
    private static readonly SourceControlPermissionSet ChangeRequestPermissions =
        new(true, false, true);
    private static readonly SourceControlPermissionSet ReviewPermissions =
        new(true, false, false, true);
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ISourceControlCredentialProvider _credentialProvider;
    private readonly GitHubOptions _options;

    public GitHubSourceControlProvider(
        HttpClient httpClient,
        ISourceControlCredentialProvider credentialProvider,
        GitHubOptions options)
    {
        if (!string.Equals(
                credentialProvider.ProviderName,
                "github",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The credential provider must support GitHub.",
                nameof(credentialProvider));
        }

        _httpClient = httpClient;
        _credentialProvider = credentialProvider;
        _options = options;
        options.ValidateApiOptions();
    }

    public string ProviderName => "github";

    public async Task<string> GetBranchHeadShaAsync(
        SourceRepositoryReference repository,
        string branchName,
        CancellationToken cancellationToken)
    {
        EnsureGitHubRepository(repository);
        GitHubHttp.ValidateBranchName(branchName);

        SourceControlCredential credential =
            await _credentialProvider.GetCredentialAsync(
                repository,
                ReadPermissions,
                cancellationToken);
        (string owner, string name) =
            GitHubHttp.ParseRepositoryId(repository.ProviderRepositoryId);
        string encodedBranch = Uri.EscapeDataString(branchName);
        Uri endpoint = GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"repos/{owner}/{name}/git/ref/heads/{encodedBranch}");

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            endpoint,
            credential,
            null,
            cancellationToken);
        EnsureSuccess(response, "get remote branch head");

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReferenceResponse? reference =
            await JsonSerializer.DeserializeAsync<GitHubReferenceResponse>(
                stream,
                SerializerOptions,
                cancellationToken);
        string sha = reference?.Object?.Sha ?? string.Empty;
        if (!GitHubHttp.IsFullGitSha(sha))
        {
            throw new InvalidDataException(
                "GitHub returned an invalid branch-head revision.");
        }

        return sha;
    }

    public async Task<ChangeRequestReference?> FindOpenChangeRequestAsync(
        SourceRepositoryReference repository,
        string sourceBranch,
        CancellationToken cancellationToken)
    {
        EnsureGitHubRepository(repository);
        GitHubHttp.ValidateBranchName(sourceBranch);
        SourceControlCredential credential =
            await _credentialProvider.GetCredentialAsync(
                repository,
                ReadPermissions,
                cancellationToken);
        return await FindOpenChangeRequestCoreAsync(
            repository,
            sourceBranch,
            credential,
            cancellationToken);
    }

    public async Task<ChangeRequestReference> CreateChangeRequestAsync(
        CreateChangeRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.SourceControlChangeRequestCreate,
            ActivityKind.Client);
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.SourceControlProvider,
            ProviderName);
        ArgumentNullException.ThrowIfNull(request);
        EnsureGitHubRepository(request.Repository);
        GitHubHttp.ValidateBranchName(request.HeadBranch);
        GitHubHttp.ValidateBranchName(request.BaseBranch);
        if (!request.IsDraft)
        {
            throw new ArgumentException(
                "The initial GitHub adapter only creates draft change requests.",
                nameof(request));
        }

        SourceControlCredential credential =
            await _credentialProvider.GetCredentialAsync(
                request.Repository,
                ChangeRequestPermissions,
                cancellationToken);

        ChangeRequestReference? existing =
            await FindOpenChangeRequestCoreAsync(
                request.Repository,
                request.HeadBranch,
                credential,
                cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        if (request.Title.Contains(
                credential.Secret,
                StringComparison.Ordinal) ||
            request.Body.Contains(
                credential.Secret,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A change-request title or body must not contain a source-control credential.");
        }

        (string owner, string name) =
            GitHubHttp.ParseRepositoryId(
                request.Repository.ProviderRepositoryId);
        Uri endpoint = GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"repos/{owner}/{name}/pulls");
        var payload = new GitHubCreatePullRequest(
            request.Title,
            request.HeadBranch,
            request.BaseBranch,
            request.Body,
            true);
        string body = JsonSerializer.Serialize(payload, SerializerOptions);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            endpoint,
            credential,
            body,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            ChangeRequestReference? raced =
                await FindOpenChangeRequestCoreAsync(
                    request.Repository,
                    request.HeadBranch,
                    credential,
                    cancellationToken);
            if (raced is not null)
            {
                return raced;
            }
        }

        EnsureSuccess(response, "create draft change request");
        return await ReadChangeRequestAsync(response, cancellationToken);
    }

    public async Task<ChangeRequestReviewResult>
        UpsertInformationalReviewAsync(
            ChangeRequestReview review,
            CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.SourceControlReviewPublish,
            ActivityKind.Client);
        MetadataOnlyTagPolicy.SetTag(
            activity,
            MetadataTag.SourceControlProvider,
            ProviderName);
        ValidateReview(review);
        SourceControlCredential credential =
            await _credentialProvider.GetCredentialAsync(
                review.Repository,
                ReviewPermissions,
                cancellationToken);
        if (review.Markdown.Contains(
                credential.Secret,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An informational review must not contain a " +
                "source-control credential.");
        }

        await EnsureExactChangeRequestRevisionAsync(
            review,
            credential,
            cancellationToken);
        IReadOnlyList<GitHubIssueComment> existing =
            await ListInformationalReviewsAsync(
                review,
                credential,
                cancellationToken);
        if (existing.Count > 1)
        {
            throw new InvalidDataException(
                "GitHub returned more than one Akos Fabric informational " +
                "review for the change request.");
        }

        string body = FormatInformationalReview(review);
        GitHubIssueComment published;
        ChangeRequestReviewPublication publication;
        if (existing.Count == 0)
        {
            published = await WriteInformationalReviewAsync(
                HttpMethod.Post,
                CreateIssueCommentsEndpoint(review),
                body,
                credential,
                cancellationToken);
            publication = ChangeRequestReviewPublication.Created;
        }
        else
        {
            (string owner, string name) = GitHubHttp.ParseRepositoryId(
                review.Repository.ProviderRepositoryId);
            Uri endpoint = GitHubHttp.CreateApiUri(
                _options.ApiBaseUrl,
                $"repos/{owner}/{name}/issues/comments/{existing[0].Id}");
            published = await WriteInformationalReviewAsync(
                HttpMethod.Patch,
                endpoint,
                body,
                credential,
                cancellationToken);
            publication = ChangeRequestReviewPublication.Updated;
        }

        return new ChangeRequestReviewResult(
            ProviderName,
            published.Id.ToString(CultureInfo.InvariantCulture),
            RequireCommentUrl(published),
            review.ChangeRequest.ProviderId,
            review.ChangeRequest.RevisionSha,
            publication);
    }

    private async Task<ChangeRequestReference?>
        FindOpenChangeRequestCoreAsync(
            SourceRepositoryReference repository,
            string sourceBranch,
            SourceControlCredential credential,
            CancellationToken cancellationToken)
    {
        (string owner, string name) =
            GitHubHttp.ParseRepositoryId(repository.ProviderRepositoryId);
        string head = Uri.EscapeDataString($"{owner}:{sourceBranch}");
        Uri endpoint = GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"repos/{owner}/{name}/pulls?state=open&head={head}&per_page=100");

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            endpoint,
            credential,
            null,
            cancellationToken);
        EnsureSuccess(response, "find open change request");

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        List<GitHubPullRequestResponse>? pullRequests =
            await JsonSerializer.DeserializeAsync<
                List<GitHubPullRequestResponse>>(
                stream,
                SerializerOptions,
                cancellationToken);

        if (pullRequests is null || pullRequests.Count == 0)
        {
            return null;
        }

        return ToReference(pullRequests[0]);
    }

    private async Task EnsureExactChangeRequestRevisionAsync(
        ChangeRequestReview review,
        SourceControlCredential credential,
        CancellationToken cancellationToken)
    {
        (string owner, string name) = GitHubHttp.ParseRepositoryId(
            review.Repository.ProviderRepositoryId);
        Uri endpoint = GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"repos/{owner}/{name}/pulls/{review.ChangeRequest.Number}");
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            endpoint,
            credential,
            null,
            cancellationToken);
        EnsureSuccess(response, "read exact change-request revision");
        ChangeRequestReference current =
            await ReadChangeRequestAsync(response, cancellationToken);
        if (!string.Equals(
                current.ProviderId,
                review.ChangeRequest.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                current.Number,
                review.ChangeRequest.Number,
                StringComparison.Ordinal)
            || !string.Equals(
                current.RevisionSha,
                review.ChangeRequest.RevisionSha,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The change-request head no longer matches the exact " +
                "revision requested for informational review.");
        }
    }

    private async Task<IReadOnlyList<GitHubIssueComment>>
        ListInformationalReviewsAsync(
            ChangeRequestReview review,
            SourceControlCredential credential,
            CancellationToken cancellationToken)
    {
        var matches = new List<GitHubIssueComment>();
        Uri? endpoint = CreateIssueCommentsEndpoint(review);
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        while (endpoint is not null)
        {
            if (!visitedPages.Add(endpoint.AbsoluteUri))
            {
                throw new InvalidDataException(
                    "GitHub returned a cyclic comment pagination link.");
            }

            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Get,
                endpoint,
                credential,
                null,
                cancellationToken);
            EnsureSuccess(response, "list informational reviews");
            await using Stream stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            List<GitHubIssueComment>? comments =
                await JsonSerializer.DeserializeAsync<
                    List<GitHubIssueComment>>(
                    stream,
                    SerializerOptions,
                    cancellationToken);
            if (comments is null)
            {
                throw new InvalidDataException(
                    "GitHub returned an empty comment-list response.");
            }

            matches.AddRange(
                comments.Where(comment =>
                    comment.Body?.Contains(
                        InformationalReviewMarker,
                        StringComparison.Ordinal) == true));
            endpoint = ReadNextPage(response);
        }

        return matches;
    }

    private async Task<GitHubIssueComment> WriteInformationalReviewAsync(
        HttpMethod method,
        Uri endpoint,
        string body,
        SourceControlCredential credential,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(
            new GitHubIssueCommentWrite(body),
            SerializerOptions);
        using HttpResponseMessage response = await SendAsync(
            method,
            endpoint,
            credential,
            payload,
            cancellationToken);
        EnsureSuccess(response, "publish informational review");
        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubIssueComment? comment =
            await JsonSerializer.DeserializeAsync<GitHubIssueComment>(
                stream,
                SerializerOptions,
                cancellationToken);
        if (comment is null
            || comment.Id <= 0
            || comment.Body is null
            || !string.Equals(comment.Body, body, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GitHub returned an invalid informational-review response.");
        }

        _ = RequireCommentUrl(comment);
        return comment;
    }

    private Uri? ReadNextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (string value in values)
        {
            foreach (string link in value.Split(','))
            {
                string[] parts = link.Split(';');
                if (parts.Length < 2
                    || !parts.Skip(1).Any(part =>
                        string.Equals(
                            part.Trim(),
                            "rel=\"next\"",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string target = parts[0].Trim();
                if (target.Length < 3
                    || target[0] != '<'
                    || target[^1] != '>'
                    || !Uri.TryCreate(
                        target[1..^1],
                        UriKind.Absolute,
                        out Uri? next))
                {
                    throw new InvalidDataException(
                        "GitHub returned an invalid comment pagination link.");
                }

                EnsureTrustedApiEndpoint(next);
                return next;
            }
        }

        return null;
    }

    private void EnsureTrustedApiEndpoint(Uri endpoint)
    {
        Uri trusted = _options.ApiBaseUrl;
        if (!string.Equals(
                endpoint.Scheme,
                trusted.Scheme,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                endpoint.Host,
                trusted.Host,
                StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != trusted.Port
            || !endpoint.AbsolutePath.StartsWith(
                trusted.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GitHub returned an untrusted comment pagination endpoint.");
        }
    }

    private Uri CreateIssueCommentsEndpoint(ChangeRequestReview review)
    {
        (string owner, string name) = GitHubHttp.ParseRepositoryId(
            review.Repository.ProviderRepositoryId);
        return GitHubHttp.CreateApiUri(
            _options.ApiBaseUrl,
            $"repos/{owner}/{name}/issues/{review.ChangeRequest.Number}/comments" +
            "?per_page=100");
    }

    private static string FormatInformationalReview(
        ChangeRequestReview review) =>
        $"{InformationalReviewMarker}\n\n" +
        $"Revision: `{review.ChangeRequest.RevisionSha}`\n\n" +
        review.Markdown.Trim();

    private static Uri RequireCommentUrl(GitHubIssueComment comment)
    {
        if (comment.HtmlUrl is null
            || !comment.HtmlUrl.IsAbsoluteUri
            || !string.Equals(
                comment.HtmlUrl.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "GitHub returned an invalid informational-review URL.");
        }

        return comment.HtmlUrl;
    }

    private static void ValidateReview(ChangeRequestReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        EnsureGitHubRepository(review.Repository);
        ArgumentNullException.ThrowIfNull(review.ChangeRequest);
        if (!string.Equals(
                review.ChangeRequest.ProviderName,
                "github",
                StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(
                review.ChangeRequest.ProviderId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long providerId)
            || providerId <= 0
            || !int.TryParse(
                review.ChangeRequest.Number,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number)
            || number <= 0
            || !GitHubHttp.IsFullGitSha(
                review.ChangeRequest.RevisionSha)
            || string.IsNullOrWhiteSpace(review.Markdown)
            || review.Markdown.Contains(
                InformationalReviewMarker,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A valid provider-neutral informational review is required.",
                nameof(review));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri endpoint,
        SourceControlCredential credential,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credential.Secret);
            GitHubHttp.AddStandardHeaders(request, _options.UserAgent);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(
                    jsonBody,
                    Encoding.UTF8,
                    "application/json");
            }

            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<ChangeRequestReference>
        ReadChangeRequestAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
    {
        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubPullRequestResponse? pullRequest =
            await JsonSerializer.DeserializeAsync<GitHubPullRequestResponse>(
                stream,
                SerializerOptions,
                cancellationToken);
        return ToReference(
            pullRequest ??
            throw new InvalidDataException(
                "GitHub returned an empty change-request response."));
    }

    private static ChangeRequestReference ToReference(
        GitHubPullRequestResponse pullRequest)
    {
        string sha = pullRequest.Head?.Sha ?? string.Empty;
        if (pullRequest.Id <= 0 ||
            pullRequest.Number <= 0 ||
            pullRequest.HtmlUrl is null ||
            !pullRequest.HtmlUrl.IsAbsoluteUri ||
            !string.Equals(
                pullRequest.HtmlUrl.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !GitHubHttp.IsFullGitSha(sha))
        {
            throw new InvalidDataException(
                "GitHub returned an invalid change-request response.");
        }

        return new ChangeRequestReference(
            "github",
            pullRequest.Id.ToString(CultureInfo.InvariantCulture),
            pullRequest.Number.ToString(CultureInfo.InvariantCulture),
            pullRequest.HtmlUrl,
            sha);
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubRequestException(
                operation,
                response.StatusCode);
        }
    }

    private static void EnsureGitHubRepository(
        SourceRepositoryReference repository) =>
        GitHubHttp.ValidateRepositoryReference(repository);

    private sealed record GitHubReferenceResponse(
        [property: JsonPropertyName("object")]
        GitHubGitObject? Object);

    private sealed record GitHubGitObject(string Sha);

    private sealed record GitHubCreatePullRequest(
        string Title,
        string Head,
        string Base,
        string Body,
        bool Draft);

    private sealed record GitHubPullRequestResponse(
        long Id,
        int Number,
        [property: JsonPropertyName("html_url")]
        Uri? HtmlUrl,
        GitHubPullRequestHead? Head);

    private sealed record GitHubPullRequestHead(string Sha);

    private sealed record GitHubIssueComment(
        long Id,
        string? Body,
        [property: JsonPropertyName("html_url")]
        Uri? HtmlUrl);

    private sealed record GitHubIssueCommentWrite(string Body);
}
