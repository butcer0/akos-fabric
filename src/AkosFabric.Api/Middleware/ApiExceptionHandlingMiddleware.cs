using AkosFabric.Application.Common.Exceptions;

namespace AkosFabric.Api.Middleware;

public sealed class ApiExceptionHandlingMiddleware
{
    private static readonly Action<ILogger, int, Exception?> LogRequestFailure =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1001, nameof(RepositorySessionException)),
            "Repository-session request failed with status {StatusCode}.");

    private static readonly Action<ILogger, int, Exception?> LogServerFailure =
        LoggerMessage.Define<int>(
            LogLevel.Error,
            new EventId(1002, nameof(RepositorySessionException)),
            "Repository-session request failed with status {StatusCode}.");

    private readonly RequestDelegate next;
    private readonly ILogger<ApiExceptionHandlingMiddleware> logger;

    public ApiExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ApiExceptionHandlingMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (RepositorySessionException exception)
        {
            int statusCode = exception switch
            {
                RepositorySessionNotFoundException =>
                    StatusCodes.Status404NotFound,
                RepositorySessionValidationException =>
                    StatusCodes.Status400BadRequest,
                RepositorySessionConflictException =>
                    StatusCodes.Status409Conflict,
                RepositorySessionPublicationException =>
                    StatusCodes.Status503ServiceUnavailable,
                RepositorySessionSynchronizationException =>
                    StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status500InternalServerError,
            };
            (statusCode >= 500 ? LogServerFailure : LogRequestFailure)(
                logger,
                statusCode,
                null);

            await Results.Problem(
                    statusCode: statusCode,
                    title: exception.GetType().Name,
                    detail: exception.Message,
                    extensions:
                    exception is RepositorySessionPublicationException publication
                        ? new Dictionary<string, object?>
                        {
                            ["repositorySessionId"] =
                                publication.RepositorySessionId,
                        }
                        : null)
                .ExecuteAsync(context);
        }
    }
}
