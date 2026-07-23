namespace AkosFabric.Application.AgentExecution.Interfaces;

public interface ILlmApiCredentialProvider
{
    Task<string> GetApiKeyAsync(
        string providerName,
        string credentialProfile,
        CancellationToken cancellationToken);
}

