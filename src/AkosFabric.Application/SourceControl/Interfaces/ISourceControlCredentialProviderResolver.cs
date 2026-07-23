namespace AkosFabric.Application.SourceControl.Interfaces;

public interface ISourceControlCredentialProviderResolver
{
    ISourceControlCredentialProvider Resolve(
        string providerName,
        string authenticationProfile);
}

