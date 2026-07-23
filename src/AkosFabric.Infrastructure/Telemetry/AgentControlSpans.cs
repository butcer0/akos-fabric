namespace AkosFabric.Infrastructure.Telemetry;

public static class AgentControlSpans
{
    public const string JiraSearch = "jira.search";
    public const string JiraIssueFetch = "jira.issue.fetch";
    public const string JiraTransition = "jira.transition";
    public const string RepositorySessionCreate = "repository_session.create";
    public const string RabbitMqPublish = "rabbitmq.publish";
    public const string RabbitMqConsume = "rabbitmq.consume";
    public const string DockerContainerStart = "docker.container.start";
    public const string DockerContainerWait = "docker.container.wait";
    public const string SourceControlCredentialAcquire =
        "source_control.credential.acquire";
    public const string SourceControlChangeRequestCreate =
        "source_control.change_request.create";
    public const string SourceControlReviewPublish =
        "source_control.review.publish";
    public const string RepositorySessionComplete =
        "repository_session.complete";

    public static IReadOnlyList<string> All { get; } =
    [
        JiraSearch,
        JiraIssueFetch,
        JiraTransition,
        RepositorySessionCreate,
        RabbitMqPublish,
        RabbitMqConsume,
        DockerContainerStart,
        DockerContainerWait,
        SourceControlCredentialAcquire,
        SourceControlChangeRequestCreate,
        SourceControlReviewPublish,
        RepositorySessionComplete,
    ];
}
