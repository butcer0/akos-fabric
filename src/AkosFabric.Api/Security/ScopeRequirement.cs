using Microsoft.AspNetCore.Authorization;

namespace AkosFabric.Api.Security;

public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;
