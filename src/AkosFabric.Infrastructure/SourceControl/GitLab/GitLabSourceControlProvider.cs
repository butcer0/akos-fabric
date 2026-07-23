using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Infrastructure.SourceControl.GitLab;

public sealed class GitLabSourceControlProvider : ISourceControlProvider
{
    public string ProviderName => "gitlab";

    public Task<ChangeRequestReference> CreateChangeRequestAsync(
        CreateChangeRequest request,
        CancellationToken cancellationToken) =>
        throw DeliveryNotImplemented();

    public Task<string> GetBranchHeadShaAsync(
        SourceRepositoryReference repository,
        string branchName,
        CancellationToken cancellationToken) =>
        throw DeliveryNotImplemented();

    public Task<ChangeRequestReference?> FindOpenChangeRequestAsync(
        SourceRepositoryReference repository,
        string sourceBranch,
        CancellationToken cancellationToken) =>
        throw DeliveryNotImplemented();

    public Task<ChangeRequestReviewResult> UpsertInformationalReviewAsync(
        ChangeRequestReview review,
        CancellationToken cancellationToken) =>
        throw DeliveryNotImplemented();

    private static NotSupportedException DeliveryNotImplemented() =>
        new(
            "GitLab delivery is intentionally not implemented in version one.");
}
