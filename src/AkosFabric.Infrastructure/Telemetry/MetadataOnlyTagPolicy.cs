using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;

namespace AkosFabric.Infrastructure.Telemetry;

public static class MetadataOnlyTagPolicy
{
    private const int MaximumStringLength = 256;
    private const long MaximumOutputByteCount = int.MaxValue;

    private static readonly FrozenDictionary<MetadataTag, string> TagNames =
        new Dictionary<MetadataTag, string>
        {
            [MetadataTag.ToolName] = "tool.name",
            [MetadataTag.Success] = "operation.success",
            [MetadataTag.ExitCode] = "process.exit.code",
            [MetadataTag.StandardOutputByteCount] = "output.stdout.bytes",
            [MetadataTag.StandardErrorByteCount] = "output.stderr.bytes",
            [MetadataTag.ModelProvider] = "gen_ai.provider.name",
            [MetadataTag.RequestModel] = "gen_ai.request.model",
            [MetadataTag.ResponseModel] = "gen_ai.response.model",
            [MetadataTag.InputTokens] = "gen_ai.usage.input_tokens",
            [MetadataTag.OutputTokens] = "gen_ai.usage.output_tokens",
            [MetadataTag.ConfiguredCostEstimate] = "gen_ai.usage.cost",
            [MetadataTag.AgentRole] = "agent.role",
            [MetadataTag.AgentPromptVersion] = "agent.prompt.version",
            [MetadataTag.SourceControlProvider] =
                "source_control.provider",
            [MetadataTag.CandidateOutcome] = "candidate.outcome",
            [MetadataTag.ChangeRequestOutcome] =
                "change_request.outcome",
        }.ToFrozenDictionary();

    private static readonly string[] UnsafeTagFragments =
    [
        "source.code",
        "source_code",
        "prompt.content",
        "model.response",
        "jira.description",
        "access_token",
        "api_key",
        "credential",
        "client_secret",
        "signing_key",
        "private_key",
        "authorization",
        "request.header.",
        "response.header.",
        "request.body",
        "response.body",
        "messaging.message.body",
        "db.statement",
        "db.query.text",
        "url.full",
        "url.query",
        "exception.message",
        "exception.stacktrace",
    ];

    public static string GetName(MetadataTag tag) =>
        TagNames.TryGetValue(tag, out string? name)
            ? name
            : throw new ArgumentOutOfRangeException(
                nameof(tag),
                tag,
                "Unknown metadata-only telemetry tag.");

    public static void SetTag(
        Activity? activity,
        MetadataTag tag,
        object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        object normalized = ValidateValue(tag, value);
        activity?.SetTag(GetName(tag), normalized);
    }

    public static bool IsExportSafe(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        return !UnsafeTagFragments.Any(fragment =>
            tagName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMetricLabelAllowed(string labelName)
    {
        if (string.IsNullOrWhiteSpace(labelName))
        {
            return false;
        }

        string normalized = labelName
            .Trim()
            .Replace('_', '.')
            .Replace('-', '.')
            .ToLowerInvariant();
        return !normalized.EndsWith(
                   "repository.session.id",
                   StringComparison.Ordinal) &&
               !normalized.EndsWith(
                   "work.item.id",
                   StringComparison.Ordinal) &&
               !normalized.EndsWith(
                   "jira.key",
                   StringComparison.Ordinal) &&
               !normalized.EndsWith(
                   "commit.sha",
                   StringComparison.Ordinal) &&
               !normalized.EndsWith(
                   "change.request.number",
                   StringComparison.Ordinal);
    }

    private static object ValidateValue(MetadataTag tag, object value) =>
        tag switch
        {
            MetadataTag.Success when value is bool => value,
            MetadataTag.ExitCode when value is int => value,
            MetadataTag.StandardOutputByteCount
                or MetadataTag.StandardErrorByteCount =>
                ValidateOutputByteCount(value),
            MetadataTag.InputTokens or MetadataTag.OutputTokens =>
                ValidateNonNegativeInteger(value, tag),
            MetadataTag.ConfiguredCostEstimate =>
                ValidateCost(value),
            _ => ValidateString(value, tag),
        };

    private static long ValidateOutputByteCount(object value)
    {
        long count = ValidateNonNegativeInteger(
            value,
            MetadataTag.StandardOutputByteCount);
        if (count > MaximumOutputByteCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Output byte counts are bounded at {MaximumOutputByteCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        return count;
    }

    private static long ValidateNonNegativeInteger(
        object value,
        MetadataTag tag)
    {
        long count = value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            _ => throw new ArgumentException(
                $"{tag} requires an integer value.",
                nameof(value)),
        };
        return count >= 0
            ? count
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                $"{tag} cannot be negative.");
    }

    private static double ValidateCost(object value)
    {
        double cost = value switch
        {
            decimal number => (double)number,
            double number => number,
            float number => number,
            _ => throw new ArgumentException(
                "Configured cost estimate requires a numeric value.",
                nameof(value)),
        };
        return double.IsFinite(cost) && cost >= 0
            ? cost
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                "Configured cost estimate must be finite and non-negative.");
    }

    private static string ValidateString(object value, MetadataTag tag)
    {
        if (value is not string text ||
            string.IsNullOrWhiteSpace(text) ||
            text.Length > MaximumStringLength ||
            text.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{tag} requires a non-empty metadata string of at most {MaximumStringLength.ToString(CultureInfo.InvariantCulture)} characters.",
                nameof(value));
        }

        return text;
    }
}

public enum MetadataTag
{
    ToolName,
    Success,
    ExitCode,
    StandardOutputByteCount,
    StandardErrorByteCount,
    ModelProvider,
    RequestModel,
    ResponseModel,
    InputTokens,
    OutputTokens,
    ConfiguredCostEstimate,
    AgentRole,
    AgentPromptVersion,
    SourceControlProvider,
    CandidateOutcome,
    ChangeRequestOutcome,
}
