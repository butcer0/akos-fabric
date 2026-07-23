using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace AkosFabric.Api.Security;

public static class AgentControlSecurityExtensions
{
    public static IServiceCollection AddAgentControlSecurity(
        this IServiceCollection services,
        ApiIdentitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.Authority.TrimEnd('/');
                options.Audience = settings.Audience;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.IncludeErrorDetails = false;
                options.RefreshOnIssuerKeyNotFound = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Authority.TrimEnd('/'),
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidTypes = ["at+jwt"],
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "sub",
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = RequiredAccessTokenClaimsValidator.ValidateAsync,
                };
            });

        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build())
            .AddPolicy(
                AgentControlPolicies.SessionsRead,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ScopeRequirement(AgentControlScopes.SessionsRead)))
            .AddPolicy(
                AgentControlPolicies.SessionsCreate,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ScopeRequirement(AgentControlScopes.SessionsCreate)))
            .AddPolicy(
                AgentControlPolicies.SessionsOperate,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ScopeRequirement(AgentControlScopes.SessionsOperate)));

        return services;
    }
}
