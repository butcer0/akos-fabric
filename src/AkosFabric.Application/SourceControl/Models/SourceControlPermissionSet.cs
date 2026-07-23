namespace AkosFabric.Application.SourceControl.Models;

public sealed record SourceControlPermissionSet(
    bool CanReadRepository,
    bool CanPushBranch,
    bool CanCreateChangeRequest,
    bool CanPublishChangeRequestReview = false);
