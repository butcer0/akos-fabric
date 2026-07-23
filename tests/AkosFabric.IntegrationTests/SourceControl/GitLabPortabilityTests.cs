using AkosFabric.Application.SourceControl.Interfaces;
using AkosFabric.Infrastructure.SourceControl.GitLab;

namespace AkosFabric.IntegrationTests.SourceControl;

public sealed class GitLabPortabilityTests
{
    [Fact]
    public void GitLabStubsImplementUnchangedNeutralApplicationBoundaries()
    {
        var sourceControl = new GitLabSourceControlProvider();
        var credentials = new GitLabCredentialProvider();
        var options = new GitLabOptions
        {
            AuthenticationProfile = "deployment-owned-profile",
        };

        Assert.IsAssignableFrom<ISourceControlProvider>(sourceControl);
        Assert.IsAssignableFrom<ISourceControlCredentialProvider>(credentials);
        Assert.Equal("gitlab", sourceControl.ProviderName);
        Assert.Equal("gitlab", credentials.ProviderName);
        Assert.Equal(
            "deployment-owned-profile",
            options.AuthenticationProfile);
    }
}
