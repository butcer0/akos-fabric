using AkosFabric.Infrastructure.Jira;

namespace AkosFabric.IntegrationTests.Jira;

public sealed class EnvironmentJiraAccessTokenProviderTests
{
    [Fact]
    public async Task ResolvesOnlyTheExactNamedExternalBinding()
    {
        var provider = new EnvironmentJiraAccessTokenProvider(
        [
            new JiraEnvironmentCredentialBinding(
                "atlassian-service-account",
                "AKOS_JIRA_ACCESS_TOKEN"),
        ],
        variable => variable == "AKOS_JIRA_ACCESS_TOKEN"
            ? "external-jira-token"
            : null);

        string token = await provider.GetAccessTokenAsync(
            "atlassian-service-account",
            CancellationToken.None);

        Assert.Equal("external-jira-token", token);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.GetAccessTokenAsync(
                "other-profile",
                CancellationToken.None));
    }

    [Fact]
    public void RejectsDuplicateAndUnsafeBindings()
    {
        Assert.Throws<ArgumentException>(
            () => new EnvironmentJiraAccessTokenProvider(
            [
                new JiraEnvironmentCredentialBinding(
                    "duplicate",
                    "TOKEN_ONE"),
                new JiraEnvironmentCredentialBinding(
                    "duplicate",
                    "TOKEN_TWO"),
            ]));
        Assert.Throws<ArgumentException>(
            () => new EnvironmentJiraAccessTokenProvider(
            [
                new JiraEnvironmentCredentialBinding(
                    "profile",
                    "TOKEN-NAME"),
            ]));
    }
}
