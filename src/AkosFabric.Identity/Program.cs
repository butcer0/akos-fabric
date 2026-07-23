using AkosFabric.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var identitySettings = IdentityHostSettings.FromConfiguration(builder.Configuration);
IdentityStartupGuard.Validate(identitySettings, builder.Environment);

if (identitySettings.Mode == IdentityHostSettings.ProductionMode)
{
    throw new InvalidOperationException(
        "Production IdentityServer stores and protected signing-key management must be configured before this host can run in Production mode.");
}

var signingKey = DevelopmentSigningKeyStore.LoadOrCreate(
    identitySettings.DevelopmentSigningKeyPath!);
var dataProtectionDirectory = Directory.CreateDirectory(
    identitySettings.DevelopmentDataProtectionKeyPath!);

builder.Services
    .AddDataProtection()
    .SetApplicationName("AkosFabric.Identity")
    .PersistKeysToFileSystem(dataProtectionDirectory);
builder.Services.AddHealthChecks();
builder.Services
    .AddIdentityServer(options =>
    {
        options.IssuerUri = identitySettings.IssuerUri.TrimEnd('/');
        options.KeyManagement.Enabled = false;
        options.EmitScopesAsSpaceDelimitedStringInJwt = true;
    })
    .AddSigningCredential(signingKey, SecurityAlgorithms.RsaSha256)
    .AddInMemoryApiScopes(IdentityConfiguration.ApiScopes)
    .AddInMemoryApiResources(IdentityConfiguration.ApiResources)
    .AddInMemoryClients(
        IdentityConfiguration.CreateDevelopmentClients(
            identitySettings.DevelopmentClientSecret!));

var app = builder.Build();

app.UseIdentityServer();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();

public partial class Program;
