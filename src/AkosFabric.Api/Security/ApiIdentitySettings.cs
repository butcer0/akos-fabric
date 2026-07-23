namespace AkosFabric.Api.Security;

public sealed record ApiIdentitySettings(
    string Mode,
    string Authority,
    string Audience,
    bool RequireHttpsMetadata)
{
    public const string DevelopmentMode = "Development";
    public const string ProductionMode = "Production";

    public static ApiIdentitySettings FromConfiguration(IConfiguration configuration) =>
        new(
            configuration["Identity:Mode"] ?? string.Empty,
            configuration["Identity:Authority"] ?? string.Empty,
            configuration["Identity:Audience"] ?? string.Empty,
            configuration.GetValue<bool>("Identity:RequireHttpsMetadata"));
}
