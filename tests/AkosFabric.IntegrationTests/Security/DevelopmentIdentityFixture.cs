using AkosFabric.Api;
using AkosFabric.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AkosFabric.IntegrationTests.Security;

public sealed class DevelopmentIdentityFixture : IDisposable
{
    public const string Authority = "https://identity.test";
    public const string ClientSecret = "integration-test-secret-with-at-least-32-characters";

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "akos-fabric-identity-tests",
        Guid.NewGuid().ToString("N"));

    public string SigningKeyPath => Path.Combine(rootPath, "signing", "identity-signing-key.pem");

    public string DataProtectionKeyPath => Path.Combine(rootPath, "data-protection");

    public WebApplicationFactory<IdentityAssembly> CreateIdentityServer() =>
        new WebApplicationFactory<IdentityAssembly>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.UseEnvironment("Development");
                builder.UseSetting("Identity:Mode", "Development");
                builder.UseSetting("Identity:IssuerUri", Authority);
                builder.UseSetting("Identity:RequireHttpsMetadata", "false");
                builder.UseSetting("Identity:Development:ClientSecret", ClientSecret);
                builder.UseSetting(
                    "Identity:Development:SigningKeyPath",
                    SigningKeyPath);
                builder.UseSetting(
                    "Identity:Development:DataProtectionKeyPath",
                    DataProtectionKeyPath);
            });

    public static WebApplicationFactory<ApiAssembly> CreateApi(
        WebApplicationFactory<IdentityAssembly> identityServer)
    {
        ArgumentNullException.ThrowIfNull(identityServer);

        return new WebApplicationFactory<ApiAssembly>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.UseEnvironment("Development");
                builder.UseSetting("Identity:Mode", "Development");
                builder.UseSetting("Identity:Authority", Authority);
                builder.UseSetting("Identity:Audience", IdentityConfiguration.ApiAudience);
                builder.UseSetting("Identity:RequireHttpsMetadata", "false");
                builder.ConfigureServices(services =>
                {
                    services.Configure<JwtBearerOptions>(
                        JwtBearerDefaults.AuthenticationScheme,
                        options =>
                        {
                            options.BackchannelHttpHandler =
                                identityServer.Server.CreateHandler();
                        });
                });
            });
    }

    public static HttpClient CreateIdentityClient(
        WebApplicationFactory<IdentityAssembly> identityServer) =>
        identityServer.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri(Authority),
                AllowAutoRedirect = false,
            });

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
