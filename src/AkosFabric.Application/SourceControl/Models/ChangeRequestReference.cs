namespace AkosFabric.Application.SourceControl.Models;

public sealed record ChangeRequestReference(
    string ProviderName,
    string ProviderId,
    string Number,
    Uri Url,
    string RevisionSha);
