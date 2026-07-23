using System.Text;

using AkosFabric.Application.AgentExecution.Models;
using AkosFabric.Infrastructure.Execution;

namespace AkosFabric.IntegrationTests.Execution;

public sealed class FileAgentSessionArtifactReaderTests
{
    private static readonly Guid SessionId =
        Guid.Parse("6a92a62a-1e93-4b5b-a52c-dcc541fb591c");
    private static readonly Guid WorkItemId =
        Guid.Parse("5e8a8ae4-65b2-4db8-aa62-949121cbd5f3");

    [Fact]
    public async Task ValidatesAndMaterializesAuthoritativeArtifacts()
    {
        string root = CreateTemporaryRoot();
        try
        {
            SessionFileStore store = CreateStore(root);
            await WriteArtifactsAsync(store, includeUnknownResultProperty: false);
            var reader = CreateReader(store);

            AgentSessionArtifactsV1 artifacts = await reader.ReadAsync(
                SessionId,
                CancellationToken.None);

            Assert.Equal(SessionId, artifacts.Manifest.RepositorySessionId);
            Assert.Equal(SessionId, artifacts.Result.RepositorySessionId);
            Assert.Equal(
                "blocked",
                Assert.Single(artifacts.Result.Items).Status);
            Assert.True(artifacts.ItemResultJson.ContainsKey(WorkItemId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsUnknownResultPropertyWithSchemaLocation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            SessionFileStore store = CreateStore(root);
            await WriteArtifactsAsync(store, includeUnknownResultProperty: true);
            var reader = CreateReader(store);

            AgentResultValidationException exception =
                await Assert.ThrowsAsync<AgentResultValidationException>(
                    () => reader.ReadAsync(
                        SessionId,
                        CancellationToken.None));

            Assert.Contains("result.json does not conform", exception.Message);
            Assert.Contains("additionalProperties", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteArtifactsAsync(
        SessionFileStore store,
        bool includeUnknownResultProperty)
    {
        string manifest =
            $$"""
            {
              "schemaVersion": 1,
              "repositorySessionId": "{{SessionId}}",
              "repositoryProfile": "akos-fabric",
              "profileRevisionSha": "{{new string('a', 40)}}",
              "imageDigest": "sha256:{{new string('b', 64)}}",
              "sourceControl": {
                "provider": "github",
                "baseUrl": "https://github.com"
              },
              "mainRepository": {
                "providerRepositoryId": "example/akos-fabric",
                "cloneUrl": "https://github.com/example/akos-fabric.git",
                "defaultBranch": "main",
                "cloneStrategy": "full",
                "gitLfs": false,
                "submodules": "none"
              },
              "supplementalRepositories": [],
              "llm": {
                "provider": "gemini",
                "modelId": "gemini-3.6-flash",
                "openHandsModel": "gemini/gemini-3.6-flash"
              },
              "workItems": [
                {
                  "workItemRunId": "{{WorkItemId}}",
                  "sequenceNumber": 1,
                  "jiraKey": "KAN-1",
                  "jiraUpdatedAt": "2026-07-23T11:45:02Z",
                  "jiraSnapshot": {}
                }
              ],
              "sessionBehavior": {
                "continueAfterItemFailure": true
              },
              "limits": {
                "sessionDeadlineSeconds": 14400,
                "maximumItems": 5,
                "maximumCostUsdPerItem": 25,
                "maximumChangedFiles": 30,
                "maximumDiffLines": 3000,
                "maximumCoderConversations": 2,
                "maximumModelCallsPerRole": 60
              }
            }
            """;
        string unknown = includeUnknownResultProperty
            ? """
              ,"unexpected": true
              """
            : string.Empty;
        string result =
            $$"""
            {
              "schemaVersion": 1,
              "repositorySessionId": "{{SessionId}}",
              "status": "completed",
              "startedAt": "2026-07-23T12:35:00Z",
              "completedAt": "2026-07-23T14:07:00Z",
              "repository": {
                "provider": "github",
                "providerRepositoryId": "example/akos-fabric",
                "cloneUrl": "https://github.com/example/akos-fabric.git"
              },
              "llm": {
                "provider": "gemini",
                "modelId": "gemini-3.6-flash"
              },
              "items": [
                {
                  "workItemRunId": "{{WorkItemId}}",
                  "jiraKey": "KAN-1",
                  "status": "blocked",
                  "changedFiles": [],
                  "modelUsage": {
                    "provider": "gemini",
                    "modelId": "gemini-3.6-flash",
                    "planner": {
                      "inputTokens": 1,
                      "outputTokens": 1,
                      "modelCalls": 1,
                      "estimatedCostUsd": 0.1
                    },
                    "coder": {
                      "inputTokens": 0,
                      "outputTokens": 0,
                      "modelCalls": 0,
                      "estimatedCostUsd": 0
                    },
                    "judge": {
                      "inputTokens": 0,
                      "outputTokens": 0,
                      "modelCalls": 0,
                      "estimatedCostUsd": 0
                    },
                    "totalEstimatedCostUsd": 0.1
                  },
                  "failureCode": "requirements_blocked",
                  "failureMessage": "Cannot proceed safely."
                }
              ]
              {{unknown}}
            }
            """;

        await store.WriteManifestAsync(
            SessionId,
            Encoding.UTF8.GetBytes(manifest),
            CancellationToken.None);
        await store.WriteResultAsync(
            SessionId,
            Encoding.UTF8.GetBytes(result),
            CancellationToken.None);
    }

    private static FileAgentSessionArtifactReader CreateReader(
        SessionFileStore store)
    {
        string repositoryRoot = FindRepositoryRoot();
        return new FileAgentSessionArtifactReader(
            store,
            new AgentSessionArtifactReaderOptions
            {
                ManifestSchemaPath = Path.Combine(
                    repositoryRoot,
                    "schemas",
                    "agent-session-manifest-v1.schema.json"),
                ResultSchemaPath = Path.Combine(
                    repositoryRoot,
                    "schemas",
                    "agent-session-result-v1.schema.json"),
            });
    }

    private static SessionFileStore CreateStore(string root) =>
        new(
            new SessionFileStoreOptions
            {
                RootDirectory = root,
                OwnerUserId = SessionFileStore.GetEffectiveUserId(),
                OwnerGroupId = SessionFileStore.GetEffectiveGroupId(),
            });

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"akos-result-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "schemas",
                        "agent-session-result-v1.schema.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Cannot locate the Akos Fabric repository root.");
    }
}
