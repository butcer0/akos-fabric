using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AkosFabric.Application.Jira.Interfaces;
using AkosFabric.Application.Jira.Models;
using AkosFabric.Application.RepositoryProfiles.Models;
using AkosFabric.Infrastructure.Telemetry;

namespace AkosFabric.Infrastructure.Jira;

public sealed class JiraClient : IJiraClient
{
    private const int SearchPageSize = 100;
    private readonly HttpClient _httpClient;
    private readonly IJiraAccessTokenProvider _accessTokenProvider;
    private readonly JiraOptions _options;

    public JiraClient(
        HttpClient httpClient,
        IJiraAccessTokenProvider accessTokenProvider,
        JiraOptions options)
    {
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
        _accessTokenProvider = accessTokenProvider
            ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _options = options
            ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<JiraIssueSnapshot>> SearchIssuesAsync(
        JiraRepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.JiraSearch,
            ActivityKind.Client);
        ArgumentNullException.ThrowIfNull(profile);
        JiraSiteOptions site = ResolveSite(profile.Site);
        string[] fields = GetRequestedFields(profile.Fields);
        var issues = new List<JiraIssueSnapshot>();
        var issueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPageTokens = new HashSet<string>(StringComparer.Ordinal);
        string? nextPageToken = null;

        do
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["jql"] = profile.SelectionJql,
                ["fields"] = fields,
                ["maxResults"] = SearchPageSize,
            };
            if (nextPageToken is not null)
            {
                body["nextPageToken"] = nextPageToken;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri(site.BaseUrl, "rest/api/3/search/jql"))
            {
                Content = JsonContent.Create(body),
            };

            using HttpResponseMessage response = await SendAsync(
                request,
                site,
                cancellationToken);
            await EnsureSuccessAsync(response, "search Jira issues", cancellationToken);

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = ParseResponse(responseJson, "Jira search");
            JsonElement root = document.RootElement;
            JsonElement issueArray = GetRequiredProperty(root, "issues", JsonValueKind.Array);

            foreach (JsonElement issue in issueArray.EnumerateArray())
            {
                JiraIssueSnapshot snapshot = ParseIssue(issue, profile);
                if (!issueKeys.Add(snapshot.Key))
                {
                    throw Terminal(
                        $"Jira search returned duplicate issue key '{snapshot.Key}'.");
                }

                issues.Add(snapshot);
            }

            bool? isLast = ReadOptionalBoolean(root, "isLast");
            string? returnedToken = ReadOptionalString(root, "nextPageToken");
            if (isLast == true)
            {
                nextPageToken = null;
            }
            else if (string.IsNullOrWhiteSpace(returnedToken))
            {
                if (isLast == false)
                {
                    throw Terminal(
                        "Jira search indicated another page but supplied no next-page token.");
                }

                nextPageToken = null;
            }
            else if (!visitedPageTokens.Add(returnedToken))
            {
                throw Terminal("Jira search repeated a next-page token.");
            }
            else
            {
                nextPageToken = returnedToken;
            }
        }
        while (nextPageToken is not null);

        return issues;
    }

    public async Task<JiraIssueSnapshot?> FindIssueAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.JiraIssueFetch,
            ActivityKind.Client);
        ArgumentNullException.ThrowIfNull(profile);
        ValidateIssueKey(profile.ProjectKey, issueKey);
        JiraSiteOptions site = ResolveSite(profile.Site);
        string fields = string.Join(',', GetRequestedFields(profile.Fields)
            .Select(Uri.EscapeDataString));
        Uri requestUri = BuildUri(
            site.BaseUrl,
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}",
            $"fields={fields}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using HttpResponseMessage response = await SendAsync(
            request,
            site,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "retrieve a Jira issue", cancellationToken);
        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = ParseResponse(responseJson, "Jira issue");
        return ParseIssue(document.RootElement, profile);
    }

    public async Task<JiraTransitionResult> TransitionIssueAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        JiraWorkflowTarget workflowTarget,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentControlTelemetry.StartActivity(
            AgentControlSpans.JiraTransition,
            ActivityKind.Client);
        ArgumentNullException.ThrowIfNull(profile);
        ValidateIssueKey(profile.ProjectKey, issueKey);
        string targetStatus = ResolveWorkflowTarget(
            profile.Workflow,
            workflowTarget);
        JiraSiteOptions site = ResolveSite(profile.Site);
        Uri transitionsUri = BuildUri(
            site.BaseUrl,
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions");

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, transitionsUri);
        using HttpResponseMessage getResponse = await SendAsync(
            getRequest,
            site,
            cancellationToken);
        await EnsureSuccessAsync(
            getResponse,
            "retrieve Jira issue transitions",
            cancellationToken);
        string responseJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = ParseResponse(responseJson, "Jira transitions");
        JsonElement transitions = GetRequiredProperty(
            document.RootElement,
            "transitions",
            JsonValueKind.Array);

        string? transitionId = transitions
            .EnumerateArray()
            .Select(ParseTransition)
            .Where(transition => string.Equals(
                transition.TargetStatus,
                targetStatus,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(transition => transition.Id, StringComparer.Ordinal)
            .Select(transition => transition.Id)
            .FirstOrDefault();

        if (transitionId is null)
        {
            return new JiraTransitionResult(
                JiraTransitionOutcome.Unavailable,
                targetStatus,
                null);
        }

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, transitionsUri)
        {
            Content = JsonContent.Create(new
            {
                transition = new { id = transitionId },
            }),
        };
        using HttpResponseMessage postResponse = await SendAsync(
            postRequest,
            site,
            cancellationToken);
        await EnsureSuccessAsync(
            postResponse,
            "transition a Jira issue",
            cancellationToken);

        return new JiraTransitionResult(
            JiraTransitionOutcome.Applied,
            targetStatus,
            transitionId);
    }

    public async Task AddCommentAsync(
        JiraRepositoryProfile profile,
        string issueKey,
        string comment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateIssueKey(profile.ProjectKey, issueKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        JiraSiteOptions site = ResolveSite(profile.Site);
        Uri requestUri = BuildUri(
            site.BaseUrl,
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment");
        object body = new
        {
            body = new
            {
                version = 1,
                type = "doc",
                content = new[]
                {
                    new
                    {
                        type = "paragraph",
                        content = new[]
                        {
                            new { type = "text", text = comment },
                        },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        using HttpResponseMessage response = await SendAsync(
            request,
            site,
            cancellationToken);
        await EnsureSuccessAsync(response, "add a Jira comment", cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        JiraSiteOptions site,
        CancellationToken cancellationToken)
    {
        string accessToken = await _accessTokenProvider.GetAccessTokenAsync(
            site.AuthenticationProfile,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken)
            || accessToken.Contains('\r', StringComparison.Ordinal)
            || accessToken.Contains('\n', StringComparison.Ordinal))
        {
            throw Terminal(
                $"Jira authentication profile '{site.AuthenticationProfile}' returned an invalid token.");
        }

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw Transient("The Jira request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw Transient("The Jira request could not be completed.", exception);
        }
    }

    private JiraSiteOptions ResolveSite(string siteName)
    {
        if (!_options.Sites.TryGetValue(siteName, out JiraSiteOptions? site))
        {
            throw Terminal($"Jira site '{siteName}' is not configured.");
        }

        if (!site.BaseUrl.IsAbsoluteUri
            || !string.Equals(site.BaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(site.BaseUrl.UserInfo)
            || !string.IsNullOrEmpty(site.BaseUrl.Query)
            || !string.IsNullOrEmpty(site.BaseUrl.Fragment))
        {
            throw Terminal(
                $"Jira site '{siteName}' must have an absolute HTTPS base URL without credentials, query, or fragment.");
        }

        if (string.IsNullOrWhiteSpace(site.AuthenticationProfile))
        {
            throw Terminal(
                $"Jira site '{siteName}' has no authentication profile.");
        }

        return site;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        JiraFailureKind failureKind = IsTransient(response.StatusCode)
            ? JiraFailureKind.Transient
            : JiraFailureKind.Terminal;
        throw new JiraClientException(
            $"Unable to {operation}; Jira returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
            failureKind,
            response.StatusCode);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static JiraIssueSnapshot ParseIssue(
        JsonElement issue,
        JiraRepositoryProfile profile)
    {
        if (issue.ValueKind != JsonValueKind.Object)
        {
            throw Terminal("Jira returned an issue that was not a JSON object.");
        }

        JsonElement fields = GetRequiredProperty(issue, "fields", JsonValueKind.Object);
        string issueId = ReadRequiredMappedString(issue, fields, profile.Fields.Id);
        string key = ReadRequiredMappedString(issue, fields, profile.Fields.Key);
        ValidateIssueKey(profile.ProjectKey, key);
        string summary = ReadRequiredMappedString(issue, fields, profile.Fields.Summary);
        JsonElement descriptionElement = GetMappedProperty(
            issue,
            fields,
            profile.Fields.Description);
        string description = ReadDescription(descriptionElement);
        string issueType = ReadRequiredDisplayName(
            GetMappedProperty(issue, fields, profile.Fields.IssueType),
            profile.Fields.IssueType);
        string status = ReadRequiredDisplayName(
            GetMappedProperty(issue, fields, profile.Fields.Status),
            profile.Fields.Status);
        string? priority = ReadOptionalDisplayName(
            GetMappedProperty(issue, fields, profile.Fields.Priority));
        IReadOnlyList<string> labels = ReadLabels(
            GetMappedProperty(issue, fields, profile.Fields.Labels),
            profile.Fields.Labels);
        string updated = ReadRequiredMappedString(
            issue,
            fields,
            profile.Fields.Updated);
        if (!DateTimeOffset.TryParse(
                updated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset updatedAt))
        {
            throw Terminal(
                $"Jira field '{profile.Fields.Updated}' is not a valid timestamp.");
        }

        return new JiraIssueSnapshot(
            issueId,
            key,
            summary,
            description,
            issueType,
            status,
            priority,
            labels,
            updatedAt,
            issue.GetRawText());
    }

    private static string ReadDescription(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Terminal("The configured Jira description field has an unsupported value.");
        }

        var builder = new StringBuilder();
        AppendAdfText(element, builder);
        return builder.ToString().TrimEnd();
    }

    private static void AppendAdfText(JsonElement element, StringBuilder builder)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? type = ReadOptionalString(element, "type");
            if (string.Equals(type, "text", StringComparison.Ordinal)
                && element.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
            else if (string.Equals(type, "hardBreak", StringComparison.Ordinal))
            {
                AppendNewLine(builder);
            }

            if (element.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in content.EnumerateArray())
                {
                    AppendAdfText(child, builder);
                }
            }

            if (type is "paragraph" or "heading" or "listItem")
            {
                AppendNewLine(builder);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                AppendAdfText(child, builder);
            }
        }
    }

    private static void AppendNewLine(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private static List<string> ReadLabels(
        JsonElement element,
        string fieldName)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Terminal($"Jira field '{fieldName}' must be an array.");
        }

        var labels = new List<string>();
        foreach (JsonElement label in element.EnumerateArray())
        {
            if (label.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(label.GetString()))
            {
                throw Terminal(
                    $"Jira field '{fieldName}' contains an invalid label.");
            }

            labels.Add(label.GetString()!);
        }

        return labels;
    }

    private static (string Id, string TargetStatus) ParseTransition(
        JsonElement transition)
    {
        if (transition.ValueKind != JsonValueKind.Object)
        {
            throw Terminal("Jira returned an invalid transition.");
        }

        string id = ReadRequiredString(transition, "id");
        JsonElement target = GetRequiredProperty(
            transition,
            "to",
            JsonValueKind.Object);
        string targetStatus = ReadRequiredString(target, "name");
        return (id, targetStatus);
    }

    private static string[] GetRequestedFields(JiraFieldProfile fields) =>
        new[]
        {
            fields.Id,
            fields.Key,
            fields.Summary,
            fields.Description,
            fields.IssueType,
            fields.Status,
            fields.Priority,
            fields.Labels,
            fields.Updated,
        }
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static JsonDocument ParseResponse(string json, string responseName)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw Terminal(
                $"{responseName} returned malformed JSON.",
                exception);
        }
    }

    private static JsonElement GetMappedProperty(
        JsonElement issue,
        JsonElement fields,
        string fieldName)
    {
        if (issue.TryGetProperty(fieldName, out JsonElement rootValue))
        {
            return rootValue;
        }

        if (fields.TryGetProperty(fieldName, out JsonElement fieldValue))
        {
            return fieldValue;
        }

        throw Terminal($"Jira response omitted configured field '{fieldName}'.");
    }

    private static string ReadRequiredMappedString(
        JsonElement issue,
        JsonElement fields,
        string fieldName)
    {
        JsonElement element = GetMappedProperty(issue, fields, fieldName);
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw Terminal(
                $"Jira field '{fieldName}' must be a non-empty string.");
        }

        return element.GetString()!;
    }

    private static string ReadRequiredDisplayName(
        JsonElement element,
        string fieldName) =>
        ReadOptionalDisplayName(element)
        ?? throw Terminal($"Jira field '{fieldName}' has no display name.");

    private static string? ReadOptionalDisplayName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(element.GetString())
                ? null
                : element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("name", out JsonElement name)
            && name.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(name.GetString())
                ? null
                : name.GetString();
        }

        return null;
    }

    private static JsonElement GetRequiredProperty(
        JsonElement parent,
        string propertyName,
        JsonValueKind expectedKind)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != expectedKind)
        {
            throw Terminal(
                $"Jira response property '{propertyName}' must be {expectedKind}.");
        }

        return property;
    }

    private static string ReadRequiredString(
        JsonElement parent,
        string propertyName)
    {
        string? value = ReadOptionalString(parent, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw Terminal(
                $"Jira response property '{propertyName}' must be a non-empty string.")
            : value;
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw Terminal(
                $"Jira response property '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private static bool? ReadOptionalBoolean(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind is not JsonValueKind.True
            and not JsonValueKind.False)
        {
            throw Terminal(
                $"Jira response property '{propertyName}' must be a boolean.");
        }

        return property.GetBoolean();
    }

    private static void ValidateIssueKey(string projectKey, string issueKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);
        string expectedPrefix = string.Concat(projectKey, "-");
        if (!issueKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || issueKey.Length == expectedPrefix.Length
            || !issueKey[expectedPrefix.Length..].All(char.IsAsciiDigit))
        {
            throw Terminal(
                $"Jira issue key '{issueKey}' is outside configured project '{projectKey}'.");
        }
    }

    private static string ResolveWorkflowTarget(
        JiraWorkflowProfile workflow,
        JiraWorkflowTarget workflowTarget) =>
        workflowTarget switch
        {
            JiraWorkflowTarget.Assigned => workflow.AssignedStatus,
            JiraWorkflowTarget.Review => workflow.ReviewStatus,
            JiraWorkflowTarget.Completed => workflow.CompletedStatus,
            JiraWorkflowTarget.Failed => workflow.FailedStatus,
            _ => throw new ArgumentOutOfRangeException(
                nameof(workflowTarget),
                workflowTarget,
                null),
        };

    private static Uri BuildUri(
        Uri baseUrl,
        string relativePath,
        string? query = null)
    {
        string normalizedBase = baseUrl.AbsoluteUri.TrimEnd('/');
        string uri = string.Concat(
            normalizedBase,
            "/",
            relativePath.TrimStart('/'));
        if (!string.IsNullOrEmpty(query))
        {
            uri = string.Concat(uri, "?", query);
        }

        return new Uri(uri, UriKind.Absolute);
    }

    private static JiraClientException Terminal(
        string message,
        Exception? innerException = null) =>
        new(message, JiraFailureKind.Terminal, innerException: innerException);

    private static JiraClientException Transient(
        string message,
        Exception? innerException = null) =>
        new(message, JiraFailureKind.Transient, innerException: innerException);
}
