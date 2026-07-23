using AkosFabric.Application.SourceControl.Models;

namespace AkosFabric.Application.SourceControl.Interfaces;

public interface ISourceControlProvider
{
    string ProviderName { get; }

    Task<ChangeRequestReference> CreateChangeRequestAsync(
        CreateChangeRequest request,
        CancellationToken cancellationToken);

    Task<string> GetBranchHeadShaAsync(
        SourceRepositoryReference repository,
        string branchName,
        CancellationToken cancellationToken);

    Task<ChangeRequestReference?> FindOpenChangeRequestAsync(
        SourceRepositoryReference repository,
        string sourceBranch,
        CancellationToken cancellationToken);

    Task<ChangeRequestReviewResult> UpsertInformationalReviewAsync(
        ChangeRequestReview review,
        CancellationToken cancellationToken);
}
