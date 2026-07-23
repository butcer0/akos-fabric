namespace AkosFabric.Application.SourceControl.Models;

public enum ChangeRequestReviewPublication
{
    Created,
    Updated,
}

public sealed record ChangeRequestReviewResult(
    string ProviderName,
    string ProviderId,
    Uri Url,
    string ChangeRequestId,
    string RevisionSha,
    ChangeRequestReviewPublication Publication);
