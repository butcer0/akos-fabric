namespace AkosFabric.Application.RepositorySessions.Models;

public sealed record CreateRepositorySessionInput(
    string RepositoryProfile,
    IReadOnlyList<string> JiraKeys);

public sealed record RepositorySessionCaller(
    string Subject,
    string ClientId,
    string? TokenId,
    string? TraceId,
    string TraceParent);
