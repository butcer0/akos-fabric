using System.Diagnostics;
using System.Security.Claims;

using AkosFabric.Api.Contracts;
using AkosFabric.Api.Security;
using AkosFabric.Application.AgentExecution.Interfaces;
using AkosFabric.Application.RepositorySessions.Interfaces;
using AkosFabric.Application.RepositorySessions.Models;
using AkosFabric.Infrastructure.Telemetry;

using Microsoft.AspNetCore.Mvc;

namespace AkosFabric.Api.Endpoints;

public static class RepositorySessionEndpoints
{
    public static IEndpointRouteBuilder MapRepositorySessionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/repository-sessions");

        group.MapPost(
                "",
                async (
                    CreateRepositorySessionRequest request,
                    ClaimsPrincipal principal,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                {
                    using Activity? activity =
                        AgentControlTelemetry.StartActivity(
                            AgentControlSpans.RepositorySessionCreate);
                    RepositorySessionDetails details = await service.CreateAsync(
                        new CreateRepositorySessionInput(
                            request.RepositoryProfile,
                            request.JiraKeys),
                        CreateCaller(principal),
                        cancellationToken);
                    AgentControlTelemetry.ApplyCorrelation(
                        activity,
                        new ControlCorrelation(details.Session.Id));
                    return Results.CreatedAtRoute(
                        "GetRepositorySession",
                        new { id = details.Session.Id },
                        RepositorySessionResponse.From(details.Session));
                })
            .RequireAuthorization(AgentControlPolicies.SessionsCreate);

        group.MapPost(
                "/{id:guid}/publish",
                async (
                    Guid id,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        RepositorySessionResponse.From(
                            (await service.PublishAsync(id, cancellationToken))
                            .Session)))
            .RequireAuthorization(AgentControlPolicies.SessionsOperate);

        group.MapPost(
                "/{id:guid}/retry",
                async (
                    Guid id,
                    ClaimsPrincipal principal,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                {
                    RepositorySessionDetails details = await service.RetryAsync(
                        id,
                        CreateCaller(principal),
                        cancellationToken);
                    return Results.CreatedAtRoute(
                        "GetRepositorySession",
                        new { id = details.Session.Id },
                        RepositorySessionResponse.From(details.Session));
                })
            .RequireAuthorization(AgentControlPolicies.SessionsOperate);

        group.MapPost(
                "/{id:guid}/cancel",
                async (
                    Guid id,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        RepositorySessionResponse.From(
                            (await service.CancelAsync(id, cancellationToken))
                            .Session)))
            .RequireAuthorization(AgentControlPolicies.SessionsOperate);

        group.MapPost(
                "/{id:guid}/reprocess-result",
                async (
                    Guid id,
                    [FromServices] IAgentResultProcessor resultProcessor,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                {
                    await resultProcessor.ProcessRecoveredAsync(
                        id,
                        cancellationToken);
                    return Results.Ok(
                        RepositorySessionResponse.From(
                            (await service.GetAsync(id, cancellationToken))
                            .Session));
                })
            .RequireAuthorization(AgentControlPolicies.SessionsOperate);

        group.MapGet(
                "",
                async (
                    [FromQuery] int limit,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        (await service.ListAsync(
                            limit == 0 ? 50 : limit,
                            cancellationToken))
                        .Select(RepositorySessionResponse.From)))
            .RequireAuthorization(AgentControlPolicies.SessionsRead);

        group.MapGet(
                "/{id:guid}",
                async (
                    Guid id,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        RepositorySessionResponse.From(
                            (await service.GetAsync(id, cancellationToken))
                            .Session)))
            .WithName("GetRepositorySession")
            .RequireAuthorization(AgentControlPolicies.SessionsRead);

        group.MapGet(
                "/{id:guid}/items",
                async (
                    Guid id,
                    [FromServices] IRepositorySessionService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(
                        (await service.ListItemsAsync(id, cancellationToken))
                        .Select(WorkItemRunResponse.From)))
            .RequireAuthorization(AgentControlPolicies.SessionsRead);

        return endpoints;
    }

    private static RepositorySessionCaller CreateCaller(ClaimsPrincipal principal)
    {
        string subject = RequiredClaim(principal, "sub");
        string clientId =
            principal.FindFirstValue("client_id")
            ?? RequiredClaim(principal, "azp");
        string? tokenId = principal.FindFirstValue("jti");
        Activity? activity = Activity.Current;
        string traceParent = activity?.Id ?? CreateTraceParent();
        return new RepositorySessionCaller(
            subject,
            clientId,
            tokenId,
            activity?.TraceId.ToHexString(),
            traceParent);
    }

    private static string RequiredClaim(
        ClaimsPrincipal principal,
        string claimType) =>
        principal.FindFirstValue(claimType)
        ?? throw new InvalidOperationException(
            $"The validated access token omitted claim '{claimType}'.");

    private static string CreateTraceParent() =>
        $"00-{Guid.NewGuid():N}-{Guid.NewGuid():N}"[..52] + "-01";
}
