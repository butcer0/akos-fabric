namespace AkosFabric.Infrastructure.Telemetry;

public static class AgentControlMetricNames
{
    public const string RepositorySessionsTotal =
        "akos_repository_sessions_total";
    public const string RepositorySessionDurationSeconds =
        "akos_repository_session_duration_seconds";
    public const string WorkItemsTotal = "akos_work_items_total";
    public const string WorkItemDurationSeconds =
        "akos_work_item_duration_seconds";
    public const string ModelRequestsTotal = "akos_model_requests_total";
    public const string ModelInputTokensTotal =
        "akos_model_input_tokens_total";
    public const string ModelOutputTokensTotal =
        "akos_model_output_tokens_total";
    public const string ModelCostUsdTotal = "akos_model_cost_usd_total";
    public const string VerificationFailuresTotal =
        "akos_verification_failures_total";
    public const string JudgeDispositionsTotal =
        "akos_judge_dispositions_total";
    public const string ChangeRequestsCreatedTotal =
        "akos_change_requests_created_total";

    public static IReadOnlyList<string> All { get; } =
    [
        RepositorySessionsTotal,
        RepositorySessionDurationSeconds,
        WorkItemsTotal,
        WorkItemDurationSeconds,
        ModelRequestsTotal,
        ModelInputTokensTotal,
        ModelOutputTokensTotal,
        ModelCostUsdTotal,
        VerificationFailuresTotal,
        JudgeDispositionsTotal,
        ChangeRequestsCreatedTotal,
    ];
}
