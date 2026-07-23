namespace AkosFabric.Application.SourceControl.Interfaces;

public interface ISourceControlProviderResolver
{
    ISourceControlProvider Resolve(string providerName);
}
