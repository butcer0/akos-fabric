using AkosFabric.Api;
using AkosFabric.Api.Endpoints;
using AkosFabric.Api.Middleware;
using AkosFabric.Api.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
var identitySettings = ApiIdentitySettings.FromConfiguration(builder.Configuration);
ApiIdentityStartupGuard.Validate(identitySettings, builder.Environment);

builder.Services.AddAgentControlSecurity(identitySettings);
builder.Services.AddAgentControl(
    builder.Configuration,
    builder.Environment);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiExceptionHandlingMiddleware>();

app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions
        {
            Predicate = _ => false,
        })
    .AllowAnonymous();
app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
            Predicate = registration =>
                registration.Tags.Contains("ready"),
        })
    .RequireAuthorization();
app.MapRepositorySessionEndpoints();

app.Run();

public partial class Program;
