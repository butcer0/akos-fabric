using System.Security.Claims;
using AkosFabric.Api.Security;
using AkosFabric.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace AkosFabric.IntegrationTests.Security;

public sealed class IdentitySecurityComponentTests
{
    public static TheoryData<string> MissingRequiredClaimCases =>
        new()
        {
            "sub",
            "iat",
            "jti",
            "client_id,azp",
            "scope,scp",
        };

    [Theory]
    [MemberData(nameof(MissingRequiredClaimCases))]
    public void RequiredClaimsValidatorRejectsMissingClaims(string omittedClaimTypes)
    {
        var omittedClaims = omittedClaimTypes.Split(',');
        var claims = new[]
        {
            new Claim("sub", "service:test"),
            new Claim("iat", "1784800000"),
            new Claim("jti", Guid.NewGuid().ToString("N")),
            new Claim("client_id", "test-client"),
            new Claim("scope", "agent.sessions.read"),
        }.Where(
            claim => !omittedClaims.Contains(
                claim.Type,
                StringComparer.Ordinal));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        Assert.False(RequiredAccessTokenClaimsValidator.HasRequiredClaims(principal));
    }

    [Fact]
    public void RequiredClaimsValidatorAcceptsAzpAndScpAliases()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim("sub", "service:test"),
                    new Claim("iat", "1784800000"),
                    new Claim("jti", Guid.NewGuid().ToString("N")),
                    new Claim("azp", "test-client"),
                    new Claim("scp", "agent.sessions.read agent.sessions.create"),
                ],
                "Bearer"));

        Assert.True(RequiredAccessTokenClaimsValidator.HasRequiredClaims(principal));
    }

    [Fact]
    public void SigningKeyIsStableAcrossReload()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "akos-fabric-signing-key-tests",
            Guid.NewGuid().ToString("N"));
        var keyPath = Path.Combine(root, "signing-key.pem");

        try
        {
            var first = DevelopmentSigningKeyStore.LoadOrCreate(keyPath);
            var second = DevelopmentSigningKeyStore.LoadOrCreate(keyPath);

            Assert.Equal(first.KeyId, second.KeyId);
            Assert.Equal(
                first.Rsa.ExportSubjectPublicKeyInfo(),
                second.Rsa.ExportSubjectPublicKeyInfo());
            Assert.True(File.Exists(keyPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DevelopmentKeyPathsInsideRepositoryAreRejected()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Development",
            ContentRootPath = Directory.GetCurrentDirectory(),
        };
        var settings = new IdentityHostSettings(
            IdentityHostSettings.DevelopmentMode,
            "https://identity.test",
            RequireHttpsMetadata: true,
            DevelopmentClientSecret: DevelopmentIdentityFixture.ClientSecret,
            DevelopmentSigningKeyPath: Path.Combine(
                Directory.GetCurrentDirectory(),
                "signing-key.pem"),
            DevelopmentDataProtectionKeyPath: Path.Combine(
                Directory.GetCurrentDirectory(),
                "data-protection"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => IdentityStartupGuard.Validate(settings, environment));

        Assert.Contains("outside the source repository", exception.Message);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void ApiProductionStartupGuardRejectsUnsafeIdentitySettings(
        string mode,
        bool requireHttpsMetadata)
    {
        var settings = new ApiIdentitySettings(
            mode,
            "https://identity.test",
            IdentityConfiguration.ApiAudience,
            requireHttpsMetadata);
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Production",
            ContentRootPath = Directory.GetCurrentDirectory(),
        };

        Assert.Throws<InvalidOperationException>(
            () => ApiIdentityStartupGuard.Validate(settings, environment));
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void IdentityProductionStartupGuardRejectsUnsafeIdentitySettings(
        string mode,
        bool requireHttpsMetadata)
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "akos-fabric-identity-guard-tests",
            Guid.NewGuid().ToString("N"));
        var settings = new IdentityHostSettings(
            mode,
            "https://identity.test",
            requireHttpsMetadata,
            DevelopmentIdentityFixture.ClientSecret,
            Path.Combine(externalRoot, "signing-key.pem"),
            Path.Combine(externalRoot, "data-protection"));
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Production",
            ContentRootPath = Directory.GetCurrentDirectory(),
        };

        Assert.Throws<InvalidOperationException>(
            () => IdentityStartupGuard.Validate(settings, environment));
    }

    [Fact]
    public void ApiStartupGuardRejectsEmptyAudience()
    {
        var settings = new ApiIdentitySettings(
            ApiIdentitySettings.DevelopmentMode,
            "https://identity.test",
            Audience: string.Empty,
            RequireHttpsMetadata: true);
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Development",
            ContentRootPath = Directory.GetCurrentDirectory(),
        };

        Assert.Throws<InvalidOperationException>(
            () => ApiIdentityStartupGuard.Validate(settings, environment));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "AkosFabric.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
