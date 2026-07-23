namespace AkosFabric.Application.SourceControl.Models;

public sealed record CreateChangeRequest(
    SourceRepositoryReference Repository,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string Body,
    bool IsDraft);
