namespace AkosFabric.Application.SourceControl.Models;

public sealed record ChangeRequestReview(
    SourceRepositoryReference Repository,
    ChangeRequestReference ChangeRequest,
    string Markdown);
